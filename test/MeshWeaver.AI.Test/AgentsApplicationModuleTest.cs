using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.AI.Application;
using MeshWeaver.Data.Completion;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The Agents application ships in the SAME assembly as the engine, and its assembly attribute is
/// what contributes it.
///
/// <para>🚨 It was dead code before the fold, and nothing said so. <c>MeshWeaver.AI.Application</c>
/// carried <c>[assembly: AgentsApplication]</c>, but a <see cref="MeshNodeProviderAttribute"/> is
/// read in exactly ONE place — <c>MeshBuilder.InstallAssemblies</c>, i.e. the
/// <c>Modules:Assemblies</c> lane — and that assembly was never listed there. It rode the app
/// closure through a ProjectReference from the portal's view pack, so it SHIPPED in every image
/// while contributing nothing: no Agents hub, no agent overview, and its two autocomplete providers
/// registered on a container that was never built. <c>ThreadChatView</c> carries the scar — it
/// news up <c>SkillAutocompleteProvider</c> by hand, with a comment explaining that resolving it
/// from DI "returned null and typing '/' showed nothing".</para>
///
/// <para>One assembly, one module, one Store entry (#2276): the attribute now sits on
/// <c>MeshWeaver.AI</c>, which the portals list under <c>Modules:Assemblies</c> — so the
/// application is contributed by the same load that brings the engine.</para>
/// </summary>
public class AgentsApplicationModuleTest
{
    [Fact]
    public void TheEngineAssembly_ContributesTheAgentsApplicationNode()
    {
        var engine = typeof(AIExtensions).Assembly;

        typeof(AgentsApplicationAttribute).Assembly.Should().BeSameAs(engine,
            "the application must travel with the engine — a second assembly would be a second "
            + "Store entry, and the whole point is that installing AI installs its app too");

        var node = engine.GetCustomAttributes<MeshNodeProviderAttribute>()
            .SelectMany(a => a.Nodes)
            .SingleOrDefault(n => n.Name == "Agents Application");

        node.Should().NotBeNull(
            "the assembly attribute is the ONLY thing that contributes the Agents application — "
            + "InstallAssemblies reads it, nothing else does");
        node!.HubConfiguration.Should().NotBeNull(
            "the node carries ConfigureAgentsApplication, which is what registers the agent "
            + "overview/details areas and the @-reference autocomplete providers");
    }
}

/// <summary>
/// The other half of the same claim, end to end: installing the engine's assembly the way the
/// Store lane installs a module BUILDS the Agents application hub, and that hub ANSWERS.
///
/// <para>An attribute that contributes a node proves the declaration; only a round trip proves the
/// hub exists and its handler is registered. This is the state the portals now configure — the
/// engine DLL listed under <c>Modules:Assemblies</c> — so the test and production install it the
/// same way.</para>
/// </summary>
public class AgentsApplicationInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Exactly what Modules:Assemblies does with "MeshWeaver.AI.dll".
            .InstallAssemblies(typeof(AIExtensions).Assembly.Location);

    [Fact(Timeout = 30000)]
    public async Task InstalledAsAModule_TheAgentsApplicationAnswers()
    {
        var response = await AwaitResponseAsync(
            new AutocompleteRequest("", null),
            o => o.WithTarget(ApplicationAddress.Agents));

        response.Message.Should().NotBeNull(
            "the Agents application hub is built from the assembly attribute's HubConfiguration — "
            + "no answer means the module lane never contributed it");
    }
}
