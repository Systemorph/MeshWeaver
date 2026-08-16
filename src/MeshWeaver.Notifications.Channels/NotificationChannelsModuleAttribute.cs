using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: MeshWeaver.Notifications.Channels.NotificationChannelsModule]

namespace MeshWeaver.Notifications.Channels;

/// <summary>
/// Module registration for the notification <b>delivery channels</b> lane. Listing this DLL under
/// <c>Modules:Assemblies</c> installs the user-authored <c>NotificationRule</c> /
/// <c>NotificationChannel</c> node types and the <see cref="NotificationTriageService"/> that
/// escalates in-app notifications to a recipient's other channels per their rules — the SAME
/// <see cref="NotificationChannelsExtensions.AddNotificationChannels"/> call a compiled-in host
/// makes, so the two registration routes cannot drift.
///
/// <para>Why this is a module and not core: the in-app bell is the always-on default and stays in
/// the platform (<c>MeshWeaver.Graph.NotificationService</c>); rules/channels + AI triage are the
/// opt-in escalation surface a deployment without outbound email has no use for. Delisting removes
/// the two node types from create/search contexts and stops triage; existing rule/channel nodes
/// remain as data. Compiled residue that stays in the platform: the
/// <c>NotificationRuleNodeType</c>/<c>NotificationChannelNodeType</c> const classes and
/// <c>NotificationService.HasRoutingRules</c> (which defers the deterministic email to triage when
/// a recipient has rule nodes).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class NotificationChannelsModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [builder => builder.AddNotificationChannels()];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="NotificationChannelsModuleAttribute"/>); a mesh that composes it explicitly — a
/// test fixture, a bespoke host — calls <see cref="AddNotificationChannels"/> for the identical
/// registration.
/// </summary>
public static class NotificationChannelsExtensions
{
    /// <summary>
    /// Registers the <c>NotificationRule</c> / <c>NotificationChannel</c> node types and the
    /// notification triage watcher on this mesh.
    /// </summary>
    public static TBuilder AddNotificationChannels<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => (TBuilder)builder
            .AddNotificationRuleType()
            .AddNotificationChannelType()
            .ConfigureServices(services =>
            {
                // Bind from the HOST's configuration through the options pipeline — there is no
                // IConfiguration in reach at install time on the boot-pack path (the same shape as
                // ObservabilityExtensions.AddLogWatch). The service self-skips at startup unless
                // Email:Enabled, mirroring the registration-time gate it had as a compiled-in
                // Portal.Shared service.
                services.AddOptions<EmailOptions>()
                    .BindConfiguration(EmailOptions.SectionName);
                return services
                    // Mesh-scoped singleton so its subscriptions live and die with the mesh
                    // (Doc/Architecture/NoStaticState.md); the IHostedService forward is what
                    // actually STARTS it — a bare singleton would never subscribe.
                    .AddSingleton<NotificationTriageService>()
                    .AddSingleton<IHostedService>(sp => sp.GetRequiredService<NotificationTriageService>());
            });
}
