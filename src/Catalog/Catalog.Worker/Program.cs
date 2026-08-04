using Aggregator.Catalog.Worker;
using Microsoft.Extensions.Hosting;
using Platform.Observability;

var builder = Host.CreateApplicationBuilder(args);
var options = CatalogWorkerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddCatalogWorker(options);
builder.Services.AddPlatformObservability(builder.Configuration, "catalog-worker");

await builder.Build().RunAsync();
