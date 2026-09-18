using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Testing.InMesh;

/// <summary>
/// What a migrated test class gets instead of booting its own monolith: the LIVE mesh the
/// <c>Tests</c> area renders in — its hub, its <see cref="IMeshService"/>, a partition of its own
/// under <see cref="TestRoot"/> so cases never collide with each other or with the mesh's content,
/// and the read/wait helpers <c>MonolithMeshTestBase</c> gave the xunit suites. Everything here
/// reads or writes the running mesh; nothing is set up and nothing is torn down (the partition is
/// recycled by the runner after the class's cases ran).
/// </summary>
public sealed class MeshTestContext
{
    /// <summary>The root every in-mesh test partition lives under.</summary>
    public const string TestRoot = "InMeshTests";

    private static readonly System.Threading.AsyncLocal<MeshTestContext?> current = new();

    /// <summary>
    /// The context of the class the runner is executing right now — for a migrated class whose
    /// constructor takes nothing (the xunit estate's plain <c>class XTest</c>), whose in-mesh base reads
    /// it here. Set by <see cref="MeshTestRunner"/> around each class; null outside a run.
    /// </summary>
    public static MeshTestContext? Current { get => current.Value; internal set => current.Value = value; }

    /// <summary>The hub the Tests area renders on — the mesh, monolith-routed in the gate.</summary>
    public IMessageHub Hub { get; }

    /// <summary>The area host, for cases that render.</summary>
    public LayoutAreaHost Host { get; }

    /// <summary>This class's own partition: <c>InMeshTests/&lt;class&gt;-&lt;random&gt;</c>.</summary>
    public string Partition { get; }

    /// <summary>Lines a case wants in the verdict's detail column (the xunit <c>ITestOutputHelper</c>).</summary>
    public Action<string> Output { get; }

    /// <summary>Per-case deadline; a case past it fails naming the deadline.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>
    /// The token of the case running right now — cancelled the moment the case's bound elapses, so a
    /// wait that observes it ends WITH the verdict instead of outliving it. The in-mesh counterpart of
    /// xunit's <c>TestContext.Current.CancellationToken</c> (xUnit1069): a timed case that never
    /// observes it is reported by <see cref="MeshTestRunner"/> as having IGNORED its cancellation,
    /// because the runner cannot stop what the case started — it can only name it. Set by the runner
    /// before each case; <see cref="System.Threading.CancellationToken.None"/> outside a case.
    /// </summary>
    public CancellationToken CancellationToken { get; internal set; }

    internal MeshTestContext(LayoutAreaHost host, string partition, Action<string> output, TimeSpan deadline)
    {
        Host = host; Hub = host.Hub; Partition = partition; Output = output; Deadline = deadline;
    }

    /// <summary>The mesh's node service.</summary>
    public IMeshService Mesh => Hub.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>A service from the mesh's container.</summary>
    public T GetRequiredService<T>() where T : notnull => Hub.ServiceProvider.GetRequiredService<T>();

    /// <summary>A path inside this class's partition.</summary>
    public string Path(string relative) => $"{Partition}/{relative.TrimStart('/')}";

    /// <summary>Creates a node under the test partition (the id and namespace are derived from <paramref name="relative"/>).</summary>
    public IObservable<MeshNode> Create(string relative, Action<MeshNode>? configure = null)
    {
        var full = Path(relative);
        var slash = full.LastIndexOf('/');
        var node = new MeshNode(full[(slash + 1)..], full[..slash]) { Name = full[(slash + 1)..], NodeType = "Markdown", State = MeshNodeState.Active };
        configure?.Invoke(node);
        return Mesh.CreateNode(node).Take(1);
    }

    /// <summary>Reads a node by full path — the first emission, bounded by the deadline.</summary>
    public IObservable<MeshNode?> Read(string path) =>
        Mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{Parent(path)} scope:children select:path,id,name,nodeType,content"))
            .Take(1)
            .Select(change => change.Items.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Timeout(Deadline);

    /// <summary>Awaits an observable's first value under the case deadline (Rx's own awaiter — no task bridge).</summary>
    public async Task<T> First<T>(IObservable<T> source) =>
        await source.Take(1).Timeout(Deadline);

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }
}
