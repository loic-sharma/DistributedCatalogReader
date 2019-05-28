using System.Net.Http;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Protocol.Catalog;

[assembly: FunctionsStartup(typeof(CatalogActorReader.Startup))]

namespace CatalogActorReader
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton<HttpClient>();
            builder.Services.AddSingleton<ICatalogClient, CatalogClient>();
        }
    }
}
