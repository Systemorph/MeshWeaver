using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The pin for <see href="https://github.com/Systemorph/MeshWeaver/issues/5958">#5958</see></b> —
/// <c>MeshNodeEditorView</c> logging <c>Error streaming node at path …</c> with
/// <c>ObjectDisposedException: The mesh-node cache was disposed; the read of '…' ended with it.</c>
///
/// <para><b>What production showed.</b> Five sightings on memex-cloud (2026-09-30 20:43Z to
/// 2026-10-02 12:39Z), each on a different pod, each inside a Roll or Restart of that instance
/// (Ops/Actions: the ci-9701 and ci-9758 rolls, the 09-30 23:53Z, 10-01 09:26Z and 10-02 12:34Z
/// restarts). The editor was not reading a cache some other code had thrown away: the node-stream
/// cache is a mesh singleton disposed with the mesh at the end of host shutdown, and it ends every
/// live read with an <see cref="ObjectDisposedException"/> (#5011). An editor still open on a pod
/// being replaced received that and reported it as a fault.</para>
///
/// <para><b>The two halves, and why both are needed.</b> When the process is LEAVING
/// (<see cref="IHostApplicationLifetime.StopApplication"/>, which is what SIGTERM triggers), the
/// cache's disposal is the end of the editor's own lifetime and its stream completes. The negative
/// control disposes the cache under a process that is NOT leaving, and the editor must still
/// ERROR — so the first test cannot pass by having stopped reporting a disposed cache at all.</para>
/// </summary>
public class ANodeEditorOpenAcrossProcessShutdownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // A real host purely for its real ApplicationLifetime — the concrete type the runtime cancels on
    // SIGTERM; it is never started, so stopping it stops nothing else.
    private readonly IHostApplicationLifetime lifetime =
        new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services.AddSingleton(lifetime));

    /// <summary>The process is leaving: the editor's stream ENDS, it does not fault.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOpenEditor_CompletesWhenTheCacheIsDisposedBecauseTheProcessIsLeaving()
    {
        var terminal = await OpenEditorThenDisposeTheCache("NodeEditorAcrossShutdown", leaving: true, TestContext.Current.CancellationToken);

        terminal.Kind.Should().Be(NotificationKind.OnCompleted,
            "a pod being rolled disposes the node-stream cache with the mesh, and an editor still "
            + "open on it must END rather than fault — memex-cloud logged this as 'Error streaming "
            + "node' on every roll and restart (#5958). Got: " + terminal.Exception?.Message);
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL. The same disposal under a process that is NOT leaving is a real
    /// defect (a cache gone while its consumers live) and must keep surfacing as one.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOpenEditor_StillFaultsWhenTheCacheIsDisposedUnderALiveProcess()
    {
        var terminal = await OpenEditorThenDisposeTheCache("NodeEditorUnderLiveProcess", leaving: false, TestContext.Current.CancellationToken);

        terminal.Kind.Should().Be(NotificationKind.OnError,
            "the leaving classification must not swallow a disposed cache under a LIVE process — "
            + "otherwise the first test passes by having stopped reporting the fault at all");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>();
    }

    private async Task<Notification<MeshNode>> OpenEditorThenDisposeTheCache(string id, bool leaving, System.Threading.CancellationToken ct)
    {
        var created = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .FirstAsync()
            .Await(ct);

        using var editor = new MeshNodeEditor(Mesh, created.Path);

        // Hot replay of the editor's terminal: subscribed BEFORE the disposal so it cannot be missed.
        var terminal = editor.Node.Materialize()
            .Where(n => n.Kind != NotificationKind.OnNext)
            .Replay(1);
        using var connection = terminal.Connect();

        // The editor must be LIVE first — a terminal seen before any snapshot would say nothing
        // about a read that was open when the cache went away.
        await editor.Node.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        if (leaving)
            lifetime.StopApplication();
        ((IDisposable)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>()).Dispose();

        return await terminal.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }
}
