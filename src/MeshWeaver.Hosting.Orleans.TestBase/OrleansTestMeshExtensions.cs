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
