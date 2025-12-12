using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NuGet.Frameworks;

namespace Aspire.Hosting;

/// <summary>
/// Extensions for building project resources.
/// </summary>
internal static class ProjectBuildExtensions
{
    /// <summary>
    /// Enables build synchronization for all <see cref="ProjectResource"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This approach builds all projects in the active launch group at startup,
    /// and coordinates individual project builds when resources are started explicitly.
    /// </para>
    /// </remarks>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="enableRestore">Whether to run restore as part of the build. Defaults to <c>true</c>.</param>
    /// <param name="captureBinLog">Whether to capture a binary log (.binlog) of the build. Defaults to <c>false</c>.</param>
    /// <returns>The builder for chaining.</returns>
    public static IDistributedApplicationBuilder AddProjectBuildSupport(this IDistributedApplicationBuilder builder, bool enableRestore = true, bool captureBinLog = false)
    {
        builder.Services.TryAddSingleton<ProjectBuildCoordinator>();
        builder.Services.Configure<ProjectBuildOptions>(options =>
        {
            options.EnableRestore = enableRestore;
            options.CaptureBinLog = captureBinLog;
            options.AppHostDirectory = builder.AppHostDirectory;
            options.TargetFramework = GetTargetFramework(builder.AppHostAssembly);
        });

        builder.Services.TryAddEventingSubscriber<ProjectBuildEventSubscriber>();

        return builder;
    }

    private static string GetTargetFramework(Assembly? appHostAssembly)
    {
        const string defaultTfm = "net10.0";

        if (appHostAssembly is null)
        {
            return defaultTfm;
        }

        var targetFrameworkAttribute = appHostAssembly.GetCustomAttribute<TargetFrameworkAttribute>();
        if (targetFrameworkAttribute?.FrameworkName is null)
        {
            return defaultTfm;
        }

        var nugetFramework = NuGetFramework.Parse(targetFrameworkAttribute.FrameworkName);
        return nugetFramework.GetShortFolderName();
    }
}

/// <summary>
/// Options for project build coordination.
/// </summary>
internal class ProjectBuildOptions
{
    /// <summary>
    /// Gets or sets whether to enable restore during build.
    /// When <c>false</c>, <c>--no-restore</c> is passed to the build command.
    /// </summary>
    public bool EnableRestore { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to capture a binary log (.binlog) of build operations.
    /// </summary>
    public bool CaptureBinLog { get; set; }

    /// <summary>
    /// Gets or sets the AppHost project directory, used as the working directory for build operations.
    /// </summary>
    public string? AppHostDirectory { get; set; }

    /// <summary>
    /// Gets or sets the target framework moniker (TFM) to use for build files.
    /// </summary>
    public string TargetFramework { get; set; } = "net10.0";
}

/// <summary>
/// Annotation to track the build state of a project resource.
/// </summary>
 internal class ProjectBuildStateAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets whether the first <see cref="BeforeResourceStartedEvent"/> has fired for this resource.
    /// </summary>
    public bool HasStartedOnce { get; set; }
}

/// <summary>
/// A wrapper around <see cref="IProjectMetadata"/> that suppresses automatic building of the project.
/// </summary>
internal class SuppressBuildProjectMetadata(IProjectMetadata inner) : IProjectMetadata
{
    /// <inheritdoc />
    public string ProjectPath => inner.ProjectPath;

    /// <inheritdoc />
    public LaunchSettings? LaunchSettings => inner.LaunchSettings;

    /// <inheritdoc />
    public bool IsFileBasedApp => inner.IsFileBasedApp;

    /// <summary>
    /// Always returns <c>true</c> to suppress automatic building.
    /// </summary>
    public bool SuppressBuild => true;
}

/// <summary>
/// Subscribes to application events to coordinate project builds.
/// </summary>
internal class ProjectBuildEventSubscriber(
    ProjectBuildCoordinator buildCoordinator,
    IConfiguration configuration) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // Subscribe to BeforeStartEvent to kick off building all projects in the active launch group in the background
        eventing.Subscribe<BeforeStartEvent>((e, ct) =>
        {
            var currentLaunchGroup = configuration.GetValue<string>("LaunchGroup");

            // Add the build state annotation and replace project metadata with suppress build wrapper
            foreach (var projectResource in e.Model.Resources.OfType<ProjectResource>())
            {
                projectResource.Annotations.Add(new ProjectBuildStateAnnotation());

                // Replace the IProjectMetadata annotation with one that suppresses automatic builds
                var existingMetadata = projectResource.Annotations.OfType<IProjectMetadata>().FirstOrDefault();
                if (existingMetadata is not null)
                {
                    projectResource.Annotations.Remove(existingMetadata);
                    projectResource.Annotations.Add(new SuppressBuildProjectMetadata(existingMetadata));
                }
            }

            // Start the build in the background - don't await it here
            buildCoordinator.StartInitialBuildAsync(e.Model, e.Services, currentLaunchGroup, ct);
            return Task.CompletedTask;
        });

        // Subscribe to BeforeResourceStartedEvent to build individual projects when started explicitly (not in initial build)
        eventing.Subscribe<BeforeResourceStartedEvent>(async (e, ct) =>
        {
            if (e.Resource is ProjectResource projectResource)
            {
                var buildStateAnnotation = projectResource.Annotations.OfType<ProjectBuildStateAnnotation>().FirstOrDefault();

                // Skip the first firing (initial startup) - only handle subsequent restarts
                if (buildStateAnnotation is not null && !buildStateAnnotation.HasStartedOnce)
                {
                    buildStateAnnotation.HasStartedOnce = true;
                    return;
                }

                await buildCoordinator.BuildProjectAsync(projectResource, e.Services, ct);
            }
        });

        return Task.CompletedTask;
    }
}

/// <summary>
/// Coordinates building of project resources, ensuring only one build operation runs at a time.
/// </summary>
internal class ProjectBuildCoordinator
{
    private readonly SemaphoreSlim _buildSemaphore = new(1, 1);
    private TaskCompletionSource? _initialBuildTcs;
    private bool _initialBuildStarted;

    /// <summary>
    /// Starts building all project resources in the active launch group in the background.
    /// </summary>
    public void StartInitialBuildAsync(
        DistributedApplicationModel model,
        IServiceProvider services,
        string? currentLaunchGroup,
        CancellationToken cancellationToken)
    {
        if (_initialBuildStarted)
        {
            return;
        }

        _initialBuildStarted = true;
        _initialBuildTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the build in the background
        _ = Task.Run(async () =>
        {
            try
            {
                await BuildAllProjectsAsync(model, services, currentLaunchGroup, cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = services.GetRequiredService<ILogger<ProjectBuildCoordinator>>();
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(ex, "Initial build failed with exception.");
                }
            }
            finally
            {
                _initialBuildTcs.TrySetResult();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Builds all project resources in the active launch group.
    /// </summary>
    private async Task BuildAllProjectsAsync(
        DistributedApplicationModel model,
        IServiceProvider services,
        string? currentLaunchGroup,
        CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<ProjectBuildCoordinator>>();
        var resourceLoggerService = services.GetRequiredService<ResourceLoggerService>();
        var resourceNotificationService = services.GetRequiredService<ResourceNotificationService>();
        var aspireStore = services.GetRequiredService<IAspireStore>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProjectBuildOptions>>().Value;

        var projectResources = model.Resources.OfType<ProjectResource>().ToList();
        if (projectResources.Count == 0)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("No project resources found to build.");
            }
            return;
        }

        await _buildSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Building {Count} project resource(s).", projectResources.Count);
            }

            // Determine which projects should be built (only those in the active launch group)
            var projectsToBuild = new List<(ProjectResource Resource, string ProjectPath)>();

            foreach (var projectResource in projectResources)
            {
                var projectPath = projectResource.GetProjectMetadata().ProjectPath;

                if (!ShouldSkipProject(projectResource, currentLaunchGroup))
                {
                    projectsToBuild.Add((projectResource, projectPath));
                }
            }

            // Save current states and set status to "Building" for each project
            var previousStates = new Dictionary<string, CustomResourceSnapshot>();
            foreach (var (resource, _) in projectsToBuild)
            {
                if (resourceNotificationService.TryGetCurrentState(resource.Name, out var currentState))
                {
                    previousStates[resource.Name] = currentState.Snapshot;
                }

                await resourceNotificationService.PublishUpdateAsync(resource, s => s with
                {
                    State = new ResourceStateSnapshot("Building", KnownResourceStateStyles.Info)
                });

                var resourceLogger = resourceLoggerService.GetLogger(resource);
                if (resourceLogger.IsEnabled(LogLevel.Information))
                {
                    resourceLogger.LogInformation("Building project...");
                }
            }

            // Generate the build file
            var buildFilePath = Path.Combine(aspireStore.BasePath, "BuildProjects.proj");
            var buildFileContent = GenerateBuildFile(projectsToBuild, options.TargetFramework);
            await File.WriteAllTextAsync(buildFilePath, buildFileContent, cancellationToken);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Generated build file at: {BuildFilePath}", buildFilePath);
            }

            // Create a dictionary of resource loggers for build output routing
            var resourceLoggers = projectsToBuild.ToDictionary(
                p => Path.GetFileName(p.ProjectPath),
                p => resourceLoggerService.GetLogger(p.Resource),
                StringComparer.OrdinalIgnoreCase);

            // Execute the build
            var binLogPath = options.CaptureBinLog ? Path.Combine(aspireStore.BasePath, "initialbuild.binlog") : null;
            var success = await ExecuteMultiProjectBuildAsync(buildFilePath, options.EnableRestore, binLogPath, options.AppHostDirectory, logger, resourceLoggers, cancellationToken);

            if (success)
            {
                // Restore previous state for each project
                foreach (var (resource, projectPath) in projectsToBuild)
                {
                    // Restore previous state only if current state is still "Building"
                    if (previousStates.TryGetValue(resource.Name, out var previousSnapshot) &&
                        resourceNotificationService.TryGetCurrentState(resource.Name, out var currentState) &&
                        currentState.Snapshot.State?.Text == "Building")
                    {
                        await resourceNotificationService.PublishUpdateAsync(resource, s => s with
                        {
                            State = previousSnapshot.State
                        });
                    }

                    var resourceLogger = resourceLoggerService.GetLogger(resource);
                    if (resourceLogger.IsEnabled(LogLevel.Information))
                    {
                        resourceLogger.LogInformation("Project built successfully.");
                    }
                }
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Successfully built {Count} project(s).", projectsToBuild.Count);
                }
            }
            else
            {
                // Log failure and restore previous state for each project resource
                foreach (var (resource, _) in projectsToBuild)
                {
                    // Restore previous state only if current state is still "Building"
                    if (previousStates.TryGetValue(resource.Name, out var previousSnapshot) &&
                        resourceNotificationService.TryGetCurrentState(resource.Name, out var currentState) &&
                        currentState.Snapshot.State?.Text == "Building")
                    {
                        await resourceNotificationService.PublishUpdateAsync(resource, s => s with
                        {
                            State = previousSnapshot.State
                        });
                    }

                    var resourceLogger = resourceLoggerService.GetLogger(resource);
                    if (resourceLogger.IsEnabled(LogLevel.Error))
                    {
                        resourceLogger.LogError("Build failed. Check coordinator logs for details.");
                    }
                }
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError("Build failed for one or more projects.");
                }
            }
        }
        finally
        {
            _buildSemaphore.Release();
        }
    }

    /// <summary>
    /// Builds a single project resource.
    /// </summary>
    public async Task BuildProjectAsync(
        ProjectResource projectResource,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var projectPath = projectResource.GetProjectMetadata().ProjectPath;
        var resourceLoggerService = services.GetRequiredService<ResourceLoggerService>();
        var resourceNotificationService = services.GetRequiredService<ResourceNotificationService>();
        var logger = resourceLoggerService.GetLogger(projectResource);

        await _buildSemaphore.WaitAsync(cancellationToken);
        try
        {
            var aspireStore = services.GetRequiredService<IAspireStore>();
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProjectBuildOptions>>().Value;

            // Save current state and set status to "Building"
            CustomResourceSnapshot? previousSnapshot = null;
            if (resourceNotificationService.TryGetCurrentState(projectResource.Name, out var currentState))
            {
                previousSnapshot = currentState.Snapshot;
            }

            await resourceNotificationService.PublishUpdateAsync(projectResource, s => s with
            {
                State = new ResourceStateSnapshot("Building", KnownResourceStateStyles.Info)
            });

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Building project at {ProjectPath}.", projectPath);
            }

            // Build the project directly
            var binLogPath = options.CaptureBinLog ? Path.Combine(aspireStore.BasePath, $"build_{projectResource.Name}.binlog") : null;
            var success = await ExecuteBuildAsync(projectPath, options.EnableRestore, binLogPath, options.AppHostDirectory, logger, cancellationToken);

            // Restore previous state only if current state is still "Building"
            if (previousSnapshot is not null &&
                resourceNotificationService.TryGetCurrentState(projectResource.Name, out var postBuildState) &&
                postBuildState.Snapshot.State?.Text == "Building")
            {
                await resourceNotificationService.PublishUpdateAsync(projectResource, s => s with
                {
                    State = previousSnapshot.State
                });
            }

            if (success)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Project built successfully.");
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError("Failed to build project.");
                }
            }
        }
        finally
        {
            _buildSemaphore.Release();
        }
    }

    private static bool ShouldSkipProject(ProjectResource projectResource, string? currentLaunchGroup)
    {
        if (string.IsNullOrEmpty(currentLaunchGroup))
        {
            return false;
        }

        var launchGroups = projectResource.Annotations.OfType<LaunchGroupAnnotation>().ToList();
        if (launchGroups.Count == 0)
        {
            // No launch group specified, always build
            return false;
        }

        // Skip if none of the project's launch groups match the current launch group
        return !launchGroups.Any(lg => lg.LaunchGroupName.Equals(currentLaunchGroup, StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateBuildFile(
        List<(ProjectResource Resource, string ProjectPath)> projectsToBuild,
        string targetFramework)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<Project Sdk="Microsoft.Build.Traversal/4.1.82">""");

        // Add project references for projects to build
        sb.AppendLine("  <ItemGroup>");
        foreach (var (resource, projectPath) in projectsToBuild)
        {
            sb.AppendLine($"""    <ProjectReference Include="{projectPath}" />""");
        }
        sb.AppendLine("  </ItemGroup>");

        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static async Task<bool> ExecuteMultiProjectBuildAsync(
        string buildFilePath,
        bool enableRestore,
        string? binLogPath,
        string? workingDirectory,
        ILogger coordinatorLogger,
        Dictionary<string, ILogger> resourceLoggers,
        CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        processStartInfo.ArgumentList.Add("build");
        processStartInfo.ArgumentList.Add(buildFilePath);
        if (!enableRestore)
        {
            processStartInfo.ArgumentList.Add("--no-restore");
        }
        if (binLogPath is not null)
        {
            processStartInfo.ArgumentList.Add($"-bl:{binLogPath}");
        }

        if (coordinatorLogger.IsEnabled(LogLevel.Debug))
        {
            coordinatorLogger.LogDebug("Executing: dotnet {Arguments}", string.Join(" ", processStartInfo.ArgumentList));
        }

        using var process = new Process { StartInfo = processStartInfo };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        string? currentProjectFile = null;

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                outputBuilder.AppendLine(e.Data);

                // Try to detect which project is currently being built from MSBuild output
                // MSBuild typically outputs lines like: "ProjectName -> output path" or includes project file names
                var detectedProject = TryDetectProjectFromOutput(e.Data, resourceLoggers.Keys);
                if (detectedProject is not null)
                {
                    currentProjectFile = detectedProject;
                }

                // Route to the appropriate logger
                if (currentProjectFile is not null && resourceLoggers.TryGetValue(currentProjectFile, out var resourceLogger))
                {
                    if (resourceLogger.IsEnabled(LogLevel.Debug))
                    {
                        resourceLogger.LogDebug("{Output}", e.Data);
                    }
                }
                else if (coordinatorLogger.IsEnabled(LogLevel.Debug))
                {
                    coordinatorLogger.LogDebug("[build] {Output}", e.Data);
                }
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                errorBuilder.AppendLine(e.Data);

                // Try to detect which project the error is for
                var detectedProject = TryDetectProjectFromOutput(e.Data, resourceLoggers.Keys);
                if (detectedProject is not null)
                {
                    currentProjectFile = detectedProject;
                }

                // Route to the appropriate logger
                if (currentProjectFile is not null && resourceLoggers.TryGetValue(currentProjectFile, out var resourceLogger))
                {
                    if (resourceLogger.IsEnabled(LogLevel.Warning))
                    {
                        resourceLogger.LogWarning("{Error}", e.Data);
                    }
                }
                else if (coordinatorLogger.IsEnabled(LogLevel.Warning))
                {
                    coordinatorLogger.LogWarning("[build:error] {Error}", e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            if (coordinatorLogger.IsEnabled(LogLevel.Error))
            {
                coordinatorLogger.LogError("Build failed with exit code {ExitCode}.\nOutput: {Output}\nErrors: {Errors}",
                    process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
            }
            return false;
        }

        return true;
    }

    private static string? TryDetectProjectFromOutput(string output, IEnumerable<string> projectFileNames)
    {
        // Look for project file names in the output
        foreach (var projectFileName in projectFileNames)
        {
            // Check for common patterns like:
            // - "ProjectName.csproj" appearing in the line
            // - "Building ProjectName..." 
            // - "ProjectName -> bin\Debug\..."
            var projectNameWithoutExtension = Path.GetFileNameWithoutExtension(projectFileName);
            if (output.Contains(projectFileName, StringComparison.OrdinalIgnoreCase) ||
                output.Contains($"{projectNameWithoutExtension} ->", StringComparison.OrdinalIgnoreCase))
            {
                return projectFileName;
            }
        }
        return null;
    }

    private static async Task<bool> ExecuteBuildAsync(
        string buildFilePath,
        bool enableRestore,
        string? binLogPath,
        string? workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        processStartInfo.ArgumentList.Add("build");
        processStartInfo.ArgumentList.Add(buildFilePath);
        if (!enableRestore)
        {
            processStartInfo.ArgumentList.Add("--no-restore");
        }
        if (binLogPath is not null)
        {
            processStartInfo.ArgumentList.Add($"-bl:{binLogPath}");
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Executing: dotnet {Arguments}", string.Join(" ", processStartInfo.ArgumentList));
        }

        using var process = new Process { StartInfo = processStartInfo };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                outputBuilder.AppendLine(e.Data);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("[build] {Output}", e.Data);
                }
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                errorBuilder.AppendLine(e.Data);
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("[build:error] {Error}", e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError("Build failed with exit code {ExitCode}.\nOutput: {Output}\nErrors: {Errors}",
                    process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
            }
            return false;
        }

        return true;
    }
}
