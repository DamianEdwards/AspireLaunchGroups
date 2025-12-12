# Aspire Launch Groups

Spike of a feature for Aspire that enables defining launch groups that resources can be assigned to. When the AppHost is started, resources not in the current launch group will be set to require explicit start.

Adapted from the [Aspire Shop sample](https://github.com/dotnet/aspire-samples/tree/main/samples/aspire-shop).

https://github.com/user-attachments/assets/99ab0430-4c31-4ae7-9640-ca0a19a2ea8d

## Overview

Launch groups allow you to organize your Aspire resources into logical groups, enabling selective startup of only the resources you need for a particular development scenario. Resources not in the active launch group will require explicit manual start from the Aspire dashboard.

## Project Build Support

When using launch groups with project resources, you can enable coordinated project building to ensure projects are built before they start:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Enable project build support - builds projects in the active launch group at startup
builder.AddProjectBuildSupport(enableRestore: true, captureBinLog: false);
```

### Parameters

- **`enableRestore`**: Whether to run `dotnet restore` as part of the build. Defaults to `true`. Set to `false` if you've already restored packages.
- **`captureBinLog`**: Whether to capture MSBuild binary logs (`.binlog`) for debugging build issues. Defaults to `false`.

### Faster AppHost Startup

By default, Aspire AppHost projects include `<ProjectReference />` elements for each project resource. This means MSBuild must build all referenced projects before the AppHost can start, even if you only want to run a subset of your services.

With project build support enabled, you can **remove the `<ProjectReference />` elements** from your AppHost's `.csproj` file and instead use path-based project references:

```csharp
// Instead of using ProjectReference in the .csproj, reference projects by path
builder.AddProject("catalogservice", "../AspireShop.CatalogService")
    .WithLaunchGroups("catalog", "frontend");
```

This provides several benefits:

1. **Faster dashboard startup**: The AppHost starts immediately without waiting for all projects to build
2. **Selective building**: Only projects in the active launch group are built
3. **On-demand rebuilds**: When you restart a project from the dashboard, it's rebuilt automatically
4. **Build status visibility**: The dashboard shows "Building" status while projects are being built

### Build Behavior

When project build support is enabled:

1. At startup, all project resources in the active launch group are built together
2. When a project resource is restarted from the dashboard, it is rebuilt before starting
3. The Aspire dashboard shows "Building" status while projects are being built
4. Build output is routed to each project resource's logs in the dashboard

This replaces Aspire's default per-project build behavior with a coordinated build that happens before any projects start.

## Defining Valid Launch Groups

To define valid launch groups for your AppHost, call `AddLaunchGroups()` on the `IDistributedApplicationBuilder` with the names of your valid launch groups:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddLaunchGroups("catalog", "basket", "frontend");
```

This serves two purposes:

1. Documents the valid launch groups for your application
2. Enables validation - if an invalid launch group is specified, a warning/error notification will be displayed

If `AddLaunchGroups()` is not called, any launch group name passed to `WithLaunchGroup()` or `WithLaunchGroups()` will be considered valid.

You can also configure valid launch groups via configuration by setting the `LaunchGroups` array:

```json
{
  "LaunchGroups": ["catalog", "basket", "frontend"]
}
```

## Assigning Resources to Launch Groups

### Single Launch Group

To assign a resource to a single launch group, use `WithLaunchGroup()`:

```csharp
builder.AddProject("frontend", "../AspireShop.Frontend")
    .WithLaunchGroup("frontend");
```

### Multiple Launch Groups

To assign a resource to multiple launch groups, use `WithLaunchGroups()`:

```csharp
var postgres = builder.AddPostgres("postgres")
    .WithLaunchGroups("catalog", "basket", "frontend");

var catalogService = builder.AddProject("catalogservice", "../AspireShop.CatalogService")
    .WithLaunchGroups("catalog", "frontend");
```

Resources assigned to multiple launch groups will start automatically when *any* of their assigned launch groups is active.

### Resources Without Launch Groups

Resources that are not assigned to any launch group will always start automatically, regardless of the active launch group.

## Configuring the Active Launch Group

The active launch group is specified via the `LaunchGroup` configuration setting. There are several ways to set this value:

### 1. Environment Variable in a Launch Profile

Add the `LaunchGroup` environment variable to a launch profile in `Properties/launchSettings.json`:

```json
{
  "profiles": {
    "Default": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17170;http://localhost:15170"
    },
    "Frontend Only": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17170;http://localhost:15170",
      "environmentVariables": {
        "...": "",
        "LaunchGroup": "frontend"
      }
    },
    "Catalog Services": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17170;http://localhost:15170",
      "environmentVariables": {
        "...": "",
        "LaunchGroup": "catalog"
      }
    }
  }
}
```

### 2. Configuration File

Add the `LaunchGroup` setting to `appsettings.json` or `appsettings.Development.json`:

```json
{
  "LaunchGroup": "frontend"
}
```

### 3. Command Line Argument

Pass the launch group as a command line argument when running the AppHost:

```bash
aspire run -- LaunchGroup=frontend
```

## Behavior

When a launch group is active:

1. Resources assigned to the active launch group will start automatically
2. Resources assigned to other launch groups (but not the active one) will be set to require explicit start
3. Resources with no launch group assignment will start automatically
4. A notification will be displayed in the Aspire dashboard indicating which launch group is active

### Validation

If `AddLaunchGroups()` was called to define valid launch groups:

- An **error** notification is shown if the active launch group doesn't match any defined launch group
- A **warning** notification is shown if any resource is assigned to a launch group that isn't in the defined list

## Example

Here's a complete example showing launch groups in action:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Enable project build support
builder.AddProjectBuildSupport();

// Define valid launch groups
builder.AddLaunchGroups("catalog", "basket", "frontend");

// Shared infrastructure - starts in all launch groups
var postgres = builder.AddPostgres("postgres")
    .WithLaunchGroups("catalog", "basket", "frontend");

var catalogDb = postgres.AddDatabase("catalogdb");

// Basket infrastructure - only needed for basket and frontend
var basketCache = builder.AddRedis("basketcache")
    .WithLaunchGroups("basket", "frontend");

// Catalog services - needed for catalog and frontend work
var catalogService = builder.AddProject("catalogservice", "../AspireShop.CatalogService")
    .WithReference(catalogDb)
    .WithLaunchGroups("catalog", "frontend");

// Basket service - needed for basket and frontend work
var basketService = builder.AddProject("basketservice", "../AspireShop.BasketService")
    .WithReference(basketCache)
    .WithLaunchGroups("basket", "frontend");

// Frontend - only in frontend launch group
builder.AddProject("frontend", "../AspireShop.Frontend")
    .WithReference(basketService)
    .WithReference(catalogService)
    .WithLaunchGroup("frontend");

builder.Build().Run();
```

With this configuration:

- **`LaunchGroup=catalog`**: Starts postgres, catalogdb, and catalogservice
- **`LaunchGroup=basket`**: Starts postgres, basketcache, and basketservice
- **`LaunchGroup=frontend`**: Starts all resources
