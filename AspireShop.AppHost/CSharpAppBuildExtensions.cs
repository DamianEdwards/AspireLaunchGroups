using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class CSharpAppBuildExtensions
{
    /// <summary>
    /// Ensures that CSharpAppResource builds are serialized so that only one build happens at a time.
    /// </summary>
    /// <remarks>
    /// This is currently required to resolve issues with concurrent builds interfering with each other, such as file locks on output assemblies.
    /// </remarks>
    public static IDistributedApplicationBuilder SerialilzeCSharpAppBuilds(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<CSharpAppResourceBuildSerializer>();

        builder.Eventing.Subscribe<BeforeResourceStartedEvent>((e, ct) =>
        {
            if (e.Resource is CSharpAppResource projectResource)
            {
                var projectBuilder = e.Services.GetRequiredService<CSharpAppResourceBuildSerializer>();
                return projectBuilder.SerializeBuild(projectResource, ct);
            }
            
            return Task.CompletedTask;
        });

        return builder;
    }

    class CSharpAppResourceBuildSerializer(ResourceLoggerService resourceLoggerService, ResourceNotificationService resourceNotificationService)
    {
        private readonly SemaphoreSlim _buildSemaphore = new(1, 1);

        public Task SerializeBuild(CSharpAppResource project, CancellationToken ct)
        {
            var logger = resourceLoggerService.GetLogger(project);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () =>
            {
                await resourceNotificationService.WaitForDependenciesAsync(project, ct);

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Waiting for build turn...");
                }
                await _buildSemaphore.WaitAsync(ct);
                tcs.SetResult();

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Finished waiting for build turn.");
                    logger.LogInformation("Waiting for resource '{ResourceName}' to reach 'Not Started' or 'Running' state before releasing build turn...", project.Name);
                }

                // Note: We have to wait for NotStarted as well as Running because resources will transition to NotStarted if they're set to explicitly start.
                await resourceNotificationService.WaitForResourceAsync(project.Name, [KnownResourceStates.NotStarted, KnownResourceStates.Running], ct);

                // Finished waiting, release the build semaphore
                _buildSemaphore.Release();
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Finished waiting for resource '{ResourceName}' to reach running state, build turn released.", project.Name);
                }
            }, ct);

            return tcs.Task;
        }
    }
}
