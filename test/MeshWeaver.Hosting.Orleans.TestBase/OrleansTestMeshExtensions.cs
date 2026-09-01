using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The mesh configuration the silo host AND the Orleans client host share in tests.
///
/// <para>🚨 <b>Deliberately free of any module's registrations.</b> This used to end
/// <c>.AddGraph().AddAI().AddKernel()</c>, which is why the whole Orleans rig lived in an AI-named
/// assembly and why ~51 tests that never touch an agent still dragged the engine in. A repo that
/// ships a module now contributes its own registrations through the <c>extra</c> parameter instead
/// (#2276) — MeshWeaver.Plugins passes <c>b =&gt; b.AddAI()</c>.</para>
/// </summary>
public static class OrleansTestMeshExtensions
{
    /// <summary>
    /// Builds the shared test mesh.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="extra">Registrations a module-owning repo adds, applied after
    /// <c>AddGraph()</c> and before <c>AddKernel()</c> — the slot <c>.AddAI()</c> used to occupy,
    /// so ordering is unchanged for a caller that supplies it.</param>
    public static MeshBuilder ConfigurePortalMesh(
        this MeshBuilder builder, Func<MeshBuilder, MeshBuilder>? extra = null)
    {
        var assemblyLocation = typeof(OrleansTestMeshExtensions).Assembly.Location;
        var configured = builder
            // 🚨 THE RIG HOSTS ITS HUBS ON AN ORLEANS CLIENT, and this is where it says so. An
            // Orleans client cannot host a grain, so IPodHubGrain.Attach can never land for a hub
            // that lives there and the memory stream is not a fallback for it — it is the only
            // transport there is. The rig puts a hub of EVERY built-in stream-routed type on its
            // cluster client:
            //   client — OrleansMeshTestBase.GetClient / SharedOrleansFixture.GetClient
            //   mesh   — the client host's own root hub, via RootMeshHubReplyStreamService
            //   portal — OrleansDocumentationTest / OrleansGraphDataTest / OrleansInteractiveMarkdownTest
            //   cache  — the client host's MeshNodeStreamCache hub
            // so all four are declared. PRODUCTION declares NONE: no production process hosts mesh
            // hubs as an Orleans client, and there a PodHubNotHere therefore means "the owner is a
            // silo whose claim has not landed", which now gets a transient NACK instead of a stream
            // publish that succeeds and discards (#2320/#2322/#2406). These declarations go away
            // when the rig hosts its hubs on a silo — at which point AddMemoryStreams goes with
            // them. See Doc/Architecture/DurableStreamsViaMeshNodes.
            .AddClientHostedAddressType("client")
            .AddClientHostedAddressType("mesh")
            .AddClientHostedAddressType("portal")
            .AddClientHostedAddressType("cache")
            .InstallAssemblies(assemblyLocation)
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/HubFactory") with
            {
                Name = "HubFactory",
                HubConfiguration = x => x
            })
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/Kernel") with
            {
                Name = "Kernel",
                HubConfiguration = x => x
            })
            // A GENERIC post NodeType — no CLR content type, so an instance keeps its Dictionary
            // content as a JsonElement, the shape production social posts have when read by code
            // that does not own their type. Registered HERE rather than on the silo configurator
            // because ConfigurePortalMesh is the config the silo host AND the Orleans client host
            // share: a node cannot be created for a type the CREATING host does not know, and the
            // client is what the tests call. Inert unless a test creates one.
            .AddMeshNodes(new MeshNode("Post", "Systemorph")
            {
                Name = "Social Media Post",
                NodeType = "NodeType",
                Content = new NodeTypeDefinition { Description = "Social media post (test fixture)." },
                HubConfiguration = config => config.AddMeshDataSource(),
            })
            .AddGraph();

        return (extra?.Invoke(configured) ?? configured).AddKernel();
    }

    /// <summary>Application-level hub configuration hook; currently a pass-through.</summary>
    public static MessageHubConfiguration ConfigureOrleansTestApplication(
        this MessageHubConfiguration configuration)
        => configuration;
}
