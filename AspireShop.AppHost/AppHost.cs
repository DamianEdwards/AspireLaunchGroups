var builder = DistributedApplication.CreateBuilder(args);

// Ensure relevant projects in the current launch group are built at AppHost startup time or before they're explicitly started
builder.AddProjectBuildSupport(enableRestore: true, captureBinLog: false);

// Define valid launch groups. Can be passed here as parameters but will also be read from IConfiguration
builder.AddLaunchGroups();

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithLaunchGroups("catalog", "basket", "frontend");

if (builder.ExecutionContext.IsRunMode)
{
    // Data volumes don't work on ACA for Postgres so only add when running
    postgres.WithDataVolume();
}

var catalogDb = postgres.AddDatabase("catalogdb");

var basketCache = builder.AddRedis("basketcache")
    .WithDataVolume()
    .WithRedisCommander()
    .WithLaunchGroups("basket", "frontend");

var catalogDbManager = builder.AddProject("catalogdbmanager", "../AspireShop.CatalogDbManager")
    .WithReference(catalogDb)
    .WaitFor(catalogDb)
    .WithHttpHealthCheck("/health")
    .WithHttpCommand("/reset-db", "Reset Database", commandOptions: new() { IconName = "DatabaseLightning" })
    .WithLaunchGroups("catalog", "frontend");

var catalogService = builder.AddProject("catalogservice", "../AspireShop.CatalogService")
    .WithReference(catalogDb)
    .WaitFor(catalogDbManager)
    .WithHttpHealthCheck("/health")
    .WithLaunchGroups("catalog", "frontend");

var basketService = builder.AddProject("basketservice", "../AspireShop.BasketService")
    .WithReference(basketCache)
    .WaitFor(basketCache)
    .WithLaunchGroups("basket", "frontend");

builder.AddProject("frontend", "../AspireShop.Frontend")
    .WithExternalHttpEndpoints()
    .WithUrlForEndpoint("https", url => url.DisplayText = "Online Store (HTTPS)")
    .WithUrlForEndpoint("http", url => url.DisplayText = "Online Store (HTTP)")
    .WithHttpHealthCheck("/health")
    .WithReference(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService)
    .WithLaunchGroup("frontend");

builder.Build().Run();
