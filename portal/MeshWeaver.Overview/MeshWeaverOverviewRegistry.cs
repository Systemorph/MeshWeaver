using System.Reflection;
using MeshWeaver.Application;
using MeshWeaver.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Overview;

[assembly:MeshWeaverOverview]

namespace MeshWeaver.Overview;

public class MeshWeaverOverviewAttribute : MeshNodeAttribute
{
    private static readonly ApplicationAddress Address = new("MeshWeaver", "Overview");
    private static readonly Assembly Assembly = typeof(MeshWeaverOverviewAttribute).Assembly;
    private static readonly string BasePath = Assembly.Location.Substring(0, Assembly.Location.LastIndexOf(Path.DirectorySeparatorChar));
    private static readonly string AssemblyPath = Assembly.Location.Substring(BasePath.Length + 1);
    private static readonly string ContentPath = "wwwroot";

    public override IMessageHub Create(IServiceProvider serviceProvider, object address)
        => serviceProvider.CreateMessageHub(Address, config => config.AddMeshWeaverOverview());

    public override MeshNode Node { get; } =
        new(Address.ToString(), "Mesh Weaver Overview", BasePath, AssemblyPath, ContentPath);
}

public static class MeshWeaverOverviewRegistry
{
    public static MessageHubConfiguration AddMeshWeaverOverview(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout)
            .AddDocumentation();
}
