using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using NuGet.Protocol.Catalog;

namespace CatalogActorReader
{
    public static class CatalogLeafProcessor
    {
        public const string FunctionName = "CatalogLeafProcessor";

        public const string PackageDeleteOperationName = nameof(CatalogLeafType.PackageDelete);
        public const string PackageDetailsOperationName = nameof(CatalogLeafType.PackageDetails);

        private static readonly HttpClient _httpClient = new HttpClient();

        [FunctionName(FunctionName)]
        public static async Task ProcessLeafAsync(
            [EntityTrigger] IDurableEntityContext ctx,
            ILogger log)
        {
            ICatalogLeafItem leaf;
            switch (ctx.OperationName)
            {
                case PackageDeleteOperationName:
                    leaf = ctx.GetInput<PackageDeleteCatalogLeaf>();
                    log.LogInformation(
                        "{CommitTimestamp}: Found package delete leaf for {PackageId} {PackageVersion}",
                        leaf.CommitTimestamp,
                        leaf.PackageId,
                        leaf.PackageVersion);
                    break;

                case PackageDetailsOperationName:
                    leaf = ctx.GetInput<PackageDetailsCatalogLeaf>();
                    log.LogInformation(
                        "{CommitTimestamp}: Found package details leaf for {PackageId} {PackageVersion}",
                        leaf.CommitTimestamp,
                        leaf.PackageId,
                        leaf.PackageVersion);
                    break;

                default:
                    throw new NotImplementedException($"Unexpected leaf type '{ctx.OperationName}'");
            }

            // TODO: Supervisor should pass its entity id down?
            await Task.Yield();

            ctx.SignalEntity(
                new EntityId(CatalogProcessor.FunctionName, "global"),
                CatalogProcessor.CompleteCatalogLeafOperationName,
                leaf);
        }
    }
}