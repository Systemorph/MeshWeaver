using MeshWeaver.AI.Plugins;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.AI.WebSearch.WebSearchModule]

namespace MeshWeaver.AI.WebSearch;

/// <summary>
/// Module registration for the agent web-search tools. Listing this DLL under
/// <c>Modules:Assemblies</c> registers <see cref="WebSearchPlugin"/> as an
/// <see cref="IAgentPlugin"/>, so an agent declaring <c>WebSearch</c> in its frontmatter gets
/// the <c>SearchWeb</c>, <c>FetchWebPage</c> and feed-reading tools.
///
/// <para>Agent plugins are resolved BY NAME out of DI (<see cref="IAgentPlugin.Name"/>), never by
/// type reference, which is what lets the whole tool family live outside the AI assembly: the
/// factory that wires an agent's tools never mentions this module, and an agent declaring
/// <c>WebSearch</c> in a deployment that does not list the DLL simply gets no such tool — the
/// same outcome as today's unconfigured deployment.</para>
///
/// <para>Configuration binds from the <c>WebSearch</c> section through the options pipeline
/// (never <c>services.Configure(section)</c> — there is no <c>IConfiguration</c> instance at
/// install time). The plugin self-gates on credentials: with no backend configured it advertises
/// no search tool at all, so listing the module in a deployment that never sets
/// <c>WebSearch:Google:*</c> changes nothing.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class WebSearchModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.AI.WebSearch")
        {
            Name = "Agent web search",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddWebSearch()),
    ];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="WebSearchModuleAttribute"/>); a mesh that composes it explicitly — a test fixture,
/// a bespoke host — calls <see cref="AddWebSearch"/> for the identical registration. The two
/// lanes must never drift, so the attribute above calls exactly this method.
/// </summary>
public static class WebSearchExtensions
{
    /// <summary>
    /// Registers the web-search agent plugin and binds <see cref="WebSearchConfiguration"/> from
    /// the <c>WebSearch</c> configuration section.
    /// </summary>
    public static IServiceCollection AddWebSearch(this IServiceCollection services)
    {
        services.AddOptions<WebSearchConfiguration>().BindConfiguration("WebSearch");
        services.AddHttpClient<WebSearchPlugin>();
        services.AddSingleton<IAgentPlugin, WebSearchPlugin>();
        return services;
    }
}
