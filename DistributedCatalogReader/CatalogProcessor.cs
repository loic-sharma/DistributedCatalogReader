using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using NuGet.Protocol.Catalog;

namespace CatalogActorReader
{
    public static class CatalogProcessor
    {
        public const string FunctionName = "CatalogProcessor";

        public const string ProcessCatalogOperationName = "ProcessCatalog";
        public const string CompleteCatalogLeafOperationName = "CompleteLeaf";

        private const int MaxPendingLeafs = 100;

        private class CatalogProcessorState
        {
            public static CatalogProcessorState Create()
            {
                return new CatalogProcessorState
                {
                    PublicCursor = DateTimeOffset.MinValue,
                    InternalCursor = DateTimeOffset.MinValue,
                    PendingLeafs = new List<ICatalogLeafItem>()
                };
            }

            /// <summary>
            /// The timestamp at which all catalog leafs have been processed.
            /// </summary>
            public DateTimeOffset PublicCursor { get; set; }

            /// <summary>
            /// The timstamp at which all catalog leafs are processed or pending processing.
            /// </summary>
            public DateTimeOffset InternalCursor { get; set; }

            /// <summary>
            /// The catalog leafs that are pending processing.
            /// </summary>
            public List<ICatalogLeafItem> PendingLeafs { get; set; }
        }

        [FunctionName(FunctionName)]
        public static async Task ProcessAsync(
            [EntityTrigger] IDurableEntityContext ctx,
            ICatalogClient catalogClient,
            ILogger log)
        {
            var state = ctx.GetState(CatalogProcessorState.Create);
            switch (ctx.OperationName)
            {
                case ProcessCatalogOperationName:
                    await ProcessCatalogAsync(ctx, catalogClient, state, log);
                    break;

                case CompleteCatalogLeafOperationName:
                    await CompleteCatalogLeafAsync(ctx, state, log);
                    break;

                default:
                    throw new NotImplementedException($"Unexpected operation name '{ctx.OperationName}'");
            }
        }

        private static async Task ProcessCatalogAsync(
            IDurableEntityContext ctx,
            ICatalogClient catalogClient,
            CatalogProcessorState state,
            ILogger log)
        {
            // Don't process any additional leaves if we are already at our maximum capacity.
            if (state.PendingLeafs.Count >= MaxPendingLeafs)
            {
                log.LogInformation(
                    "{PendingLeafs} pending leafs, reached maximum of {MaxPendingLeafs} pending leaves",
                    state.PendingLeafs.Count,
                    MaxPendingLeafs);
                return;
            }

            log.LogInformation("Finding pages to process after internal cursor {InternalCursor}...", state.InternalCursor);

            var index = await catalogClient.GetIndexAsync("https://api.nuget.org/v3/catalog0/index.json");

            var pageItems = index.GetPagesInBounds(state.InternalCursor, DateTimeOffset.MaxValue);
            if (pageItems.Count == 0)
            {
                log.LogInformation("Found no pages after internal cursor {InternalCursor}", state.InternalCursor);
                return;
            }

            foreach (var pageItem in pageItems)
            {
                log.LogInformation(
                    "Finding leaves to process on page {PageUrl} after internal cursor {InternalCursor}...",
                    pageItem.Url,
                    state.InternalCursor);

                if (state.PendingLeafs.Count >= MaxPendingLeafs)
                {
                    log.LogInformation(
                        "{PendingLeafs} pending leafs, reached maximum of {MaxPendingLeafs} pending leaves",
                        state.PendingLeafs.Count,
                        MaxPendingLeafs);
                    return;
                }

                var page = await catalogClient.GetPageAsync(pageItem.Url);
                var leafItems = page.GetLeavesInBounds(state.InternalCursor, DateTimeOffset.MaxValue, excludeRedundantLeaves: true);

                if (leafItems.Count == 0)
                {
                    log.LogInformation("Found no pages after internal cursor {InternalCursor}", state.InternalCursor);
                    return;
                }

                foreach (var leafItem in leafItems)
                {
                    log.LogInformation(
                        "Processing leaf {LeafType} for {PackageId} {PackageVersion} at {CommitTimestamp}...",
                        leafItem.Type,
                        leafItem.PackageId,
                        leafItem.PackageVersion,
                        leafItem.CommitTimestamp);

                    state.PendingLeafs.Add(leafItem);
                    ctx.SignalEntity(
                        new EntityId(CatalogLeafProcessor.FunctionName, leafItem.PackageId.ToLowerInvariant()),
                        leafItem.Type switch
                        {
                            CatalogLeafType.PackageDelete => CatalogLeafProcessor.PackageDeleteOperationName,
                            CatalogLeafType.PackageDetails => CatalogLeafProcessor.PackageDetailsOperationName,

                            _ => throw new NotImplementedException($"Unexpected leaf type '{leafItem.Type}'")
                        });
                }

                state.InternalCursor = state.PendingLeafs.Max(l => l.CommitTimestamp);
                ctx.SetState(state);

                log.LogInformation(
                    "Done processing leafs from page {PageUrl} with internal curosr {InternalCursor}",
                    pageItem.Url,
                    state.InternalCursor);
            }

            log.LogInformation("Done processing pages with internal cursor {InternalCursor}", state.InternalCursor);
        }

        private static async Task CompleteCatalogLeafAsync(
            IDurableEntityContext ctx,
            CatalogProcessorState state,
            ILogger log)
        {
            var completedLeaf = ctx.GetInput<ICatalogLeafItem>();

            var removed = state.PendingLeafs.RemoveAll(pendingLeaf => AreEqual(completedLeaf, pendingLeaf));

            // The processed leaf may not be pending. This may happen if the processor's state
            // failed to save after a message was sent to the leaf processor.
            if (removed == 0)
            {
                log.LogWarning(
                    "Ignoring completed leaf {PackageId} {PackageVersion} {CommitTimestamp}",
                    completedLeaf.PackageId,
                    completedLeaf.PackageVersion,
                    completedLeaf.CommitTimestamp);
                return;
            }

            if (removed != 1)
            {
                throw new InvalidOperationException("A leaf should match to zero or one pending leaves!");
            }

            var latestPublicCursor = GetLatestPublicCursor(state, completedLeaf);

            // TODO: Persist public cursor to blob storage
            log.LogInformation("Updating pending leafs, setting public cursor to {LatestPublicCursor}...", latestPublicCursor);
            ctx.SetState(state);
            await Task.Yield();
        }

        private static bool AreEqual(ICatalogLeafItem left, ICatalogLeafItem right)
        {
            if (left.Type != right.Type) return false;
            if (left.CommitTimestamp != right.CommitTimestamp) return false;

            if (left.PackageId != right.PackageId) return false;
            if (left.PackageVersion != right.PackageVersion) return false;

            return true;
        }

        private static DateTimeOffset GetLatestPublicCursor(CatalogProcessorState state, ICatalogLeafItem completedLeaf)
        {
            if (state.PublicCursor > completedLeaf.CommitTimestamp)
            {
                throw new ArgumentException("The public cursor should never be greater than any of the pending catalog leafs!");
            }

            // If this was the last pending leaf, update the cursor to the greatest processed timestamp.
            // This greatest processed timestamp may be greater than the completed leaf's timestamp.
            if (!state.PendingLeafs.Any())
            {
                return state.InternalCursor;
            }

            // Move the cursor forward to the completed leaf's timestamp if this was the oldest pending leaf.
            if (!state.PendingLeafs.Any(l => l.CommitTimestamp <= completedLeaf.CommitTimestamp))
            {
                return completedLeaf.CommitTimestamp;
            }

            // Otherwise, leave the cursor unchanged.
            return state.PublicCursor;
        }
    }
}