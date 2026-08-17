using MeshWeaver.AI.Plugins;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: MeshWeaver.Mail.MicrosoftGraph.MailMicrosoftGraphMeshModule]
[assembly: MeshWeaver.Mail.MicrosoftGraph.MailMicrosoftGraphEndpointModule]

namespace MeshWeaver.Mail.MicrosoftGraph;

/// <summary>
/// The mesh half of the Microsoft Graph mail module: system email (<c>IEmailSender</c> over
/// Graph <c>/sendMail</c>), inbound mail intake, and the Executive Assistant's mailbox tools.
///
/// <para><b>Why this is a module.</b> The Microsoft Graph SDK is the single heaviest dependency
/// the portal carried — 43 MB across nine assemblies — for four files of code, and
/// <c>Microsoft.Graph.dll</c> alone materializes a <b>41 MiB native metadata block</b> in every
/// Roslyn script reference set (see <c>KernelScriptReferences</c>, where it is named as a direct
/// cause of CI memory-pressure flakes). A deployment that sends no mail should carry none of it.</para>
///
/// <para>The seam already existed: <see cref="IEmailSender"/> and <see cref="EmailOptions"/> live
/// in the mesh contract, and <c>HubEmailExtensions</c> resolves the sender OPTIONALLY — yielding
/// false rather than throwing when nothing is registered. So the host keeps every mail-shaped
/// feature that is SDK-free (the invitation emailer, the outbound drain, the no-op fallback) and
/// this module supplies the transport.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MailMicrosoftGraphMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Mail.MicrosoftGraph")
        {
            Name = "Microsoft Graph mail",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddMailMicrosoftGraph()),
    ];
}

/// <summary>
/// The endpoint half: <c>POST /api/email</c>, the Graph change-notification webhook.
/// This is the module's OWN protocol surface — Graph posts to it only because this module created
/// the subscription — so delisting removing the route (a 404) is the right semantic.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MailMicrosoftGraphEndpointModuleAttribute : MeshEndpointProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
    [
        endpoints => endpoints.MapEmailWebhook(),
    ];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="MailMicrosoftGraphMeshModuleAttribute"/>); a fixture or bespoke host calls
/// <see cref="AddMailMicrosoftGraph"/> for the identical registration — the two lanes must never
/// drift.
/// </summary>
public static class MailMicrosoftGraphExtensions
{
    /// <summary>
    /// Registers the Graph mail transport, the inbound intake, and the Executive Assistant's
    /// mailbox tools.
    ///
    /// <para><b>Self-gating on <c>Email:Enabled</c>.</b> Everything here is registered
    /// unconditionally as a TYPE but the senders/watchers no-op unless mail is configured, exactly
    /// as before the extraction: <see cref="GraphSubscriptionService"/> already self-skips unless
    /// <c>Email:Enabled &amp;&amp; Email:InboundEnabled</c>.</para>
    ///
    /// <para>🚨 <see cref="IEmailSender"/> is registered with a plain <c>AddSingleton</c> while the
    /// host registers its no-op fallback with <c>TryAddSingleton</c>. That pairing is
    /// ORDER-INDEPENDENT and deliberate: whichever runs first, the last registration of a service
    /// type is the one <c>GetRequiredService</c> returns, and <c>TryAdd</c> declines when any
    /// registration already exists. Module listed ⇒ Graph sender wins; module absent ⇒ the host's
    /// no-op keeps the two <c>GetRequiredService&lt;IEmailSender&gt;</c> call sites (the invitation
    /// emailer and the outbound drain) resolvable instead of throwing at startup.</para>
    /// </summary>
    public static IServiceCollection AddMailMicrosoftGraph(this IServiceCollection services)
    {
        services.AddSingleton<GraphMail>();
        services.AddSingleton<IEmailSender, GraphEmailSender>();
        // 🚨 The PORTAL hub explicitly, never an ambient IMessageHub: inbound mail finds-or-creates
        // conversation threads, so it must run on the same hub the portal's threads live on. The
        // host's registration passed PortalApplication.Hub for exactly this reason; letting DI pick
        // whatever IMessageHub happens to be registered would route intake at a different address.
        services.AddSingleton(sp => new EmailInboundProcessor(
            sp.GetRequiredService<PortalApplication>().Hub,
            sp.GetRequiredService<GraphMail>(),
            sp.GetService<ILogger<EmailInboundProcessor>>()));
        services.AddHostedService<GraphSubscriptionService>();
        // The Executive Assistant's mailbox tools. Resolved BY NAME out of DI like every agent
        // plugin, so nothing in the AI assembly references this type. Its per-user delegated
        // token comes from IEaGraphAuth — an SDK-free seam in the mesh contract, implemented by
        // the host (which owns the OAuth consent controller and the credential node).
        services.AddSingleton<IAgentPlugin, ExecutiveAssistantPlugin>();
        return services;
    }
}
