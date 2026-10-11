using UrlShortener.Infrastructure;

// Separate deployable so analytics ingestion scales and fails independently of redirects (URL-11).
// Same configuration keys as the API (12-factor): Database__Provider, ConnectionStrings__Links, Kafka__*.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddClickConsumer();

var host = builder.Build();
await host.RunAsync();
