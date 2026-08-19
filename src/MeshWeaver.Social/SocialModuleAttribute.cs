using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

[assembly: MeshWeaver.Social.SocialMeshModule]
[assembly: MeshWeaver.Social.SocialModule]

namespace MeshWeaver.Social;

/// <summary>
/// The mesh half of the Social module: listing <c>MeshWeaver.Social.dll</c> under
/// <c>Modules:Assemblies</c> registers the LinkedIn publishing services and node-menu
/// providers via <see cref="SocialExtensions.AddSocial"/> — the same configure path a
/// fixture or bespoke host calls explicitly (the two lanes must never drift).
///
/// <para>The <c>ApiCredential</c> NodeType registration deliberately does NOT ride this
/// module: it stays with the host (Memex <c>ApiCredentialNodeType</c>) so existing
/// credential nodes keep deserializing even when the module is delisted.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SocialMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [SocialExtensions.AddSocial];
}

/// <summary>
/// The endpoint half of the Social module (design #1655 — the first consumer of the
/// endpoint-contribution hook): the LinkedIn connect, company-Page sync, and member
/// publish/engagement routes map through the host's <c>app.MapMeshModuleEndpoints()</c>.
/// Delisting the module removes the routes wholesale — a 404, not a compiled
/// optional-service 503.
///
/// <para>The LinkedIn <b>sign-in</b> scheme (<c>AddLinkedInAuthentication</c>) is NOT part
/// of this module: authentication schemes configure before the host builds, so that
/// registration stays platform. Only the endpoints ride the module lane.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SocialModuleAttribute : MeshEndpointProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
    [
        endpoints => endpoints.MapLinkedInConnect(),
        endpoints => endpoints.MapLinkedInPageSync(),
        endpoints => endpoints.MapLinkedInPublish(),
    ];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="SocialMeshModuleAttribute"/>); a mesh that composes it explicitly — a test
/// fixture, a bespoke host — calls <see cref="AddSocial"/> for the identical registration.
/// </summary>
public static class SocialExtensions
{
    /// <summary>
    /// Registers the Social module's DI surface: <see cref="LinkedInOptions"/> bound from
    /// <c>Social:LinkedIn</c> through the options pipeline, the <see cref="LinkedInPublisher"/>
    /// typed HTTP client, and the LinkedIn node-menu providers. Everything stays inert while
    /// <c>Social:LinkedIn:ClientId</c> is unconfigured — the menu providers emit no items and
    /// the endpoints answer with a configuration problem.
    /// </summary>
    public static MeshBuilder AddSocial(this MeshBuilder builder)
        => builder
            // The credential type this module reads and writes. Registered by the module rather
            // than the host: without it the connect callbacks fail with "NodeType 'ApiCredential'
            // is not registered", and with it host-side the portal had to compile against this
            // module for a single type.
            .AddApiCredentialType()
            .ConfigureServices(services =>
        {
            // Options pipeline, never services.Configure(section): there is no IConfiguration
            // instance at install time (Doc/Architecture/Modules). The bare-instance bridge keeps
            // LinkedInPublisher's ctor (and its tests) on the plain LinkedInOptions shape.
            services.AddOptions<LinkedInOptions>().BindConfiguration(LinkedInOptions.SectionName);
            services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<LinkedInOptions>>().Value);
            services.AddHttpClient<LinkedInPublisher>();

            // "Link LinkedIn account" / "Download past posts" on the viewer's own user +
            // credential nodes, and Publish/Refresh-engagement on social-media Post nodes.
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<INodeMenuProvider, LinkedInCredentialMenuProvider>());
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<INodeMenuProvider, SocialPostMenuProvider>());

            // The TIMED twin of that Publish menu item. Singleton, not scoped: EventSubscriptionRunner
            // is a hosted service resolving out of the ROOT provider, and a scoped registration there
            // throws at the moment a slot fires — i.e. in production, hours after any test ran.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IEventContinuationHandler, ScheduledSocialPublishHandler>());

            // …and the half that ARMS it. Without this the handler above is unreachable: nothing else
            // in the mesh reads a post's scheduledAt, which is precisely how posts sat "Scheduled"
            // and never went out.
            services.AddHostedService<ScheduledPostWatcher>();
            return services;
        });
}
