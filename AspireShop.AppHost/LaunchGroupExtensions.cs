using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

internal static class LaunchGroupExtensions
{
    /// <summary>
    /// Adds support for resource launch groups for the distributed application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Launch groups allow resources to be grouped together so that only resources in the current launch group will start automatically.
    /// Resources not in the current launch group will need to be started explicitly.
    /// </para>
    /// <para>
    /// Valid launch groups can be configured by calling this method and using the <c>LaunchGroups</c> configuration setting, or by calling this method with the desired launch group names.
    /// </para>
    /// <para>
    /// If this method isn't called, any launch group name passed to <see cref="WithLaunchGroup"/> will be considered valid.
    /// </para>
    /// <para>
    /// The current launch group is specified via the <c>LaunchGroup</c> configuration setting, which can be set as an environment variable, e.g. in a launch profle,
    /// in a configuration file, e.g. <i>appsettings.Development.json</i>, or passed as a command line argument, e.g. <c>dotnet run -- LaunchGroup=frontend</c>
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder.</param>
    /// <param name="launchGroupNames">The valid launch group names.</param>
    /// <returns>The builder.</returns>
    public static IDistributedApplicationBuilder AddLaunchGroups(this IDistributedApplicationBuilder builder, params string[] launchGroupNames)
    {
        builder.Services.AddOptions<LaunchGroupsOptions>()
            .BindConfiguration("") // Bind to the root
            .Configure(options =>
            {
                foreach (var launchGroupName in launchGroupNames)
                {
                    options.LaunchGroups.Add(launchGroupName);
                }
            });

        return builder;
    }

    /// <summary>
    /// Specifies that this resource should only start in the specified launch groups. If the current launch group doesn't match, the resource will need to be explicity started.
    /// </summary>
    /// <remarks>
    /// The current launch group is specified via the <c>LaunchGroup</c> configuration setting, which can be set as an environment variable, e.g. in a launch profle, or passed as a 
    /// command line argument, e.g.
    /// <example>
    /// <code>dotnet run -- LaunchGroup=frontend</code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The builder.</param>
    /// <param name="launchGroupNames">The launch group names that this resource should start in.</param>
    /// <returns>The builder.</returns>
    public static IResourceBuilder<T> WithLaunchGroups<T>(this IResourceBuilder<T> builder, params string[] launchGroupNames)
        where T : Resource
    {
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<LaunchGroupResourceEventSubscriber>();

        foreach (var launchGroupName in launchGroupNames)
        {
            builder.WithAnnotation(new LaunchGroupAnnotation(launchGroupName), ResourceAnnotationMutationBehavior.Append);
        }

        var currentLaunchGroup = builder.ApplicationBuilder.Configuration.GetValue<string>("LaunchGroup");
        if (!string.IsNullOrEmpty(currentLaunchGroup) && !launchGroupNames.Contains(currentLaunchGroup, StringComparer.OrdinalIgnoreCase))
        {
            builder.WithExplicitStart();
        }

        return builder;
    }

    /// <summary>
    /// Specifies that this resource should only start in the specified launch group. If the current launch group doesn't match, the resource will need to be explicity started.
    /// </summary>
    /// <remarks>
    /// The current launch group is specified via the <c>LaunchGroup</c> configuration setting, which can be set as an environment variable, e.g. in a launch profle, or passed as a 
    /// command line argument, e.g.
    /// <example>
    /// <code>dotnet run -- LaunchGroup=frontend</code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The builder.</param>
    /// <param name="launchGroupName">The launch group name that this resource should start in.</param>
    /// <returns>The builder.</returns>
    public static IResourceBuilder<T> WithLaunchGroup<T>(this IResourceBuilder<T> builder, string launchGroupName)
        where T : Resource
    {
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<LaunchGroupResourceEventSubscriber>();

        builder.WithAnnotation(new LaunchGroupAnnotation(launchGroupName), ResourceAnnotationMutationBehavior.Append);

        var currentLaunchGroup = builder.ApplicationBuilder.Configuration.GetValue<string>("LaunchGroup");
        if (!string.IsNullOrEmpty(currentLaunchGroup) &&
            !launchGroupName.Equals(currentLaunchGroup, StringComparison.OrdinalIgnoreCase))
        {
            builder.WithExplicitStart();
        }

        return builder;
    }

    class LaunchGroupsOptions
    {
        public HashSet<string> LaunchGroups { get; } = [];
    }

    class LaunchGroupResourceEventSubscriber(IOptions<LaunchGroupsOptions> launchGroupsOptions, IConfiguration configuration) : IDistributedApplicationEventingSubscriber
    {
        public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            // Hook the global BeforeStartEvent to validate launch groups and display notices (fired once per AppHost start)
            eventing.Subscribe<BeforeStartEvent>(async (e, ct) =>
            {
                var currentLaunchGroup = configuration.GetValue<string>("LaunchGroup");
                var interactionService = e.Services.GetRequiredService<IInteractionService>();
                var logger = e.Services.GetRequiredService<ILogger<DistributedApplication>>();

                // Get defined launch groups if any
                var definedLaunchGroups = launchGroupsOptions.Value.LaunchGroups;
                var launchGroupValid = true;
                if (definedLaunchGroups.Count > 0)
                {
                    // Verify the current launch group is valid
                    if (!string.IsNullOrEmpty(currentLaunchGroup) &&
                        !definedLaunchGroups.Contains(currentLaunchGroup, StringComparer.OrdinalIgnoreCase))
                    {
                        launchGroupValid = false;
                        var errorMessage = $"""
                            The current launch group *{currentLaunchGroup}* does not match any of the launch groups defined in the AppHost:
                            - {string.Join("\n- ", definedLaunchGroups)}

                            Please update the `LaunchGroup` configuration setting to use a valid launch group, or update the AppHost to allow this launch group.
                            """;

                        if (logger.IsEnabled(LogLevel.Error))
                        {
                            logger.LogError("Invalid launch group configuration: {Message}", errorMessage);
                        }

                        if (interactionService.IsAvailable)
                        {
                            _ = interactionService.PromptNotificationAsync(
                                $"'{currentLaunchGroup}' Launch Group Invalid",
                                errorMessage,
                                new NotificationInteractionOptions { ShowDismiss = false, EnableMessageMarkdown = true, Intent = MessageIntent.Error },
                                ct);
                        }
                    }

                    // Verify all launch group annotations are valid
                    var resourcesWithInvalidLaunchGroups = e.Model.Resources
                        .Select(r => (Resource: r, LaunchGroups: r.Annotations.OfType<LaunchGroupAnnotation>()))
                        .Where(t => t.LaunchGroups.Any(l => !definedLaunchGroups.Contains(l.LaunchGroupName, StringComparer.OrdinalIgnoreCase)))
                        .ToList();

                    if (resourcesWithInvalidLaunchGroups.Count > 0)
                    {
                        var warningMessage = $"""
                            One or more resources have launch groups that do not match any defined launch groups:
                              - Defined launch groups: {string.Join(", ", definedLaunchGroups)}.
                              - Invalid resources: {string.Join(", ", resourcesWithInvalidLaunchGroups.Select(t => $"{t.Resource.Name} (Launch Groups: {string.Join("|", t.LaunchGroups.Select(lg => lg.LaunchGroupName))})"))}
                            """;

                        if (logger.IsEnabled(LogLevel.Warning))
                        {
                            logger.LogWarning("Invalid launch group configuration: {Message}", warningMessage);
                        }

                        if (interactionService.IsAvailable)
                        {
                            _ = interactionService.PromptNotificationAsync(
                                "Invalid Launch Groups",
                                warningMessage,
                                new NotificationInteractionOptions { EnableMessageMarkdown = true, Intent = MessageIntent.Warning },
                                ct);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(currentLaunchGroup) && launchGroupValid)
                {
                    // Display a notice about the current launch group
                    if (interactionService.IsAvailable)
                    {
                        _ = interactionService.PromptNotificationAsync(
                            $"'{currentLaunchGroup}' Launch Group Active",
                            $"Only resources configured for the launch group *{currentLaunchGroup}*, or with no launch group configured, will start automatically. Other resources can be started explicitly.",
                            new NotificationInteractionOptions { EnableMessageMarkdown = true, Intent = MessageIntent.Information },
                            ct);
                    }

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Launch Group '{LaunchGroup}' is active. Only resources configured to start in this launch group will start automatically. Other resources can be started explicitly.", currentLaunchGroup);
                    }
                }
            });

            return Task.CompletedTask;
        }
    }
}

internal class LaunchGroupAnnotation(string launchGroupName) : IResourceAnnotation
{
    public string LaunchGroupName { get; } = launchGroupName;
}
