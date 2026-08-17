using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: MeshWeaver.Teams.TeamsMeshModule]
[assembly: MeshWeaver.Teams.TeamsEndpointModule]

namespace MeshWeaver.Teams;

/// <summary>
/// The mesh half of the Teams module: the Bot Framework client, the inbound router that turns a
/// Teams message into an agent thread round, and the proactive reply sender.
///
/// <para>Teams is a channel a deployment either has or does not: it needs an Azure Bot resource and
/// a published Teams app, which most deployments never provision. Listing the DLL is now that
/// decision. The <c>TeamsConversation</c> NodeType deliberately does NOT ride this module — it
/// stays in <c>MeshWeaver.Graph</c> so existing conversation nodes keep deserializing when the
/// module is delisted, the same rule <c>MeshWeaver.Social</c> follows for its credential type.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TeamsMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Teams")
        {
            Name = "Microsoft Teams channel",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddTeams()),
    ];
}

/// <summary>
/// The endpoint half: <c>POST /api/teams/messages</c>, the Bot Framework messaging endpoint.
/// The module's OWN protocol surface — Microsoft posts to it only because this module registered a
/// bot — so delisting removing the route is the right semantic.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TeamsEndpointModuleAttribute : MeshEndpointProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
    [
        endpoints => endpoints.MapTeamsBot(),
    ];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="TeamsMeshModuleAttribute"/>); a fixture or bespoke host calls <see cref="AddTeams"/>
/// for the identical registration — the two lanes must never drift.
/// </summary>
public static class TeamsExtensions
{
    /// <summary>
    /// Registers the Teams client, the inbound router and the reply sender, binding
    /// <see cref="TeamsOptions"/> from the <c>Teams</c> section through the options pipeline.
    ///
    /// <para>Everything stays INERT unless the bot credentials are configured: the client reports
    /// <see cref="ITeamsClient.IsConfigured"/> false, the messaging endpoint answers 404, and the
    /// reply sender self-skips. That mirrors the host's previous behaviour exactly — the client and
    /// router were always registered so the endpoint could resolve them and answer 404, and only
    /// the hosted reply sender was feature-gated.</para>
    /// </summary>
    public static IServiceCollection AddTeams(this IServiceCollection services)
    {
        services.AddOptions<TeamsOptions>().BindConfiguration(TeamsOptions.SectionName);
        // The bare-instance bridge: TeamsClient and the reply sender take a plain TeamsOptions,
        // and their tests construct it directly.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TeamsOptions>>().Value);
        services.AddHttpClient<ITeamsClient, TeamsClient>();
        // 🚨 The PORTAL hub explicitly, never an ambient IMessageHub: an inbound Teams message
        // finds-or-creates a conversation thread, so it must run on the hub those threads live on.
        services.AddSingleton(sp => new TeamsInboundProcessor(
            sp.GetRequiredService<PortalApplication>().Hub,
            sp.GetRequiredService<ITeamsClient>(),
            sp.GetService<ILogger<TeamsInboundProcessor>>()));
        services.AddHostedService<TeamsReplySender>();
        return services;
    }
}
