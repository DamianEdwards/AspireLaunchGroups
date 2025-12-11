using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static partial class CSharpAppBuildExtensions
{
    /// <summary>
    /// Ensures that CSharpAppResource builds are serialized so that only one build happens at a time.
    /// </summary>
    /// <remarks>
    /// This is currently required to resolve issues with concurrent builds interfering with each other, such as file locks on output assemblies.
    /// </remarks>
    public static IDistributedApplicationBuilder SerializeCSharpAppBuilds(this IDistributedApplicationBuilder builder)
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

    partial class CSharpAppResourceBuildSerializer(ResourceLoggerService resourceLoggerService, ResourceNotificationService resourceNotificationService)
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
                    logger.LogInformation("Waiting for build turn.");
                }
                await _buildSemaphore.WaitAsync(ct);

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Finished waiting for build turn.");
                    logger.LogInformation("Waiting for resource '{ResourceName}' to finish building before releasing build turn.", project.Name);
                }

                var logTask = WaitForLogAsync(project, "info:", ct);
                var healthyTask = resourceNotificationService.WaitForResourceHealthyAsync(project.Name, ct);
                // Note: We have to wait for NotStarted as well as Running because resources will transition to NotStarted if they're set to explicitly start.
                var terminalStateTask = resourceNotificationService.WaitForResourceAsync(
                    project.Name,
                    [KnownResourceStates.NotStarted, ..KnownResourceStates.TerminalStates],
                    ct);

                // Unblock resource from starting
                tcs.SetResult();

                var completedTask = await Task.WhenAny(healthyTask, terminalStateTask, logTask);

                // Finished waiting, release the build semaphore
                _buildSemaphore.Release();
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var reason = completedTask switch
                    {
                        _ when completedTask == healthyTask => "became healthy",
                        _ when completedTask == terminalStateTask => $"reached the {terminalStateTask.Result} state",
                        _ when completedTask == logTask => "logged 'info:'",
                        _ => "completed"
                    };
                    logger.LogInformation(
                        "Finished waiting for resource '{ResourceName}' to finish building because it {Reason}, build turn released.",
                        project.Name,
                        reason);
                }
            }, ct);

            return tcs.Task;
        }

        private async Task WaitForLogAsync(IResource resource, string log, CancellationToken cancellationToken)
        {
            await foreach (var entry in resourceLoggerService.WatchAsync(resource).WithCancellation(cancellationToken))
            {
                if (entry.Any(line => StripAnsiCodes(line.Content).Contains(log, StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }
            }
        }

        private static string StripAnsiCodes(string input)
        {
            return AnsiCodesRegex().Replace(input, string.Empty);
        }

        [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
        private static partial System.Text.RegularExpressions.Regex AnsiCodesRegex();
    }
}
