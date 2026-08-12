using System.Reactive;
using System.Reactive.Subjects;

namespace MeshWeaver.Graph;

/// <summary>
/// The boot signal "the static repo import has SETTLED" — mesh-scoped, one-shot, reactive.
///
/// <para>🚨 Why this exists. <c>StaticRepoImportHostedService</c> kicks
/// <see cref="StaticRepoImporter.ImportAll"/> fire-and-forget onto the thread pool and returns
/// immediately (it must: subscribing on the startup thread re-enters the hub schedulers and
/// deadlocks). The NodeType pre-warm sweep then starts from <c>ApplicationStarted</c> — which fires
/// as soon as every <c>StartAsync</c> has returned, i.e. while the import is still landing content
/// — and enumerates the fleet with ONE snapshot (<c>Query(...).Take(1)</c>). Whatever the import
/// has not written yet is silently absent from that snapshot, so those NodeTypes are never baked
/// and each one compiles on a user's first click instead.</para>
///
/// <para>That is a RACE, and it was observed resolving both ways on memex-cloud on 2026-08-12: the
/// pod started at 23:02 enumerated <b>237</b> types (the whole statically-served
/// <c>MeshWeaver/…</c> tree missing), while its successor at 05:52 enumerated <b>279</b> — same
/// content, same image family. The 42 unbaked types are the "waiting for code" complaint: they
/// compile on the activation path, one at a time, when someone opens them.</para>
///
/// <para><b>Settles on failure too, deliberately.</b> A faulted import must not park the bake
/// forever — the sweep proceeds and bakes what IS there. This is a one-shot ordering signal, never
/// a retry or a timeout: subscribers get the current state immediately once settled
/// (<see cref="AsyncSubject{T}"/> replays to late subscribers), so there is nothing to poll.</para>
/// </summary>
public sealed class StaticRepoImportSettled
{
    private readonly AsyncSubject<Unit> settled = new();

    /// <summary>
    /// Completes once the boot import has settled — successfully, with an error, or immediately
    /// when there is nothing to import. Replays to subscribers that arrive after the fact.
    /// </summary>
    public IObservable<Unit> Settled => settled;

    /// <summary>Whether <see cref="MarkSettled"/> has already run (diagnostics/logging only).</summary>
    public bool IsSettled => settled.IsCompleted;

    /// <summary>
    /// Signals that the boot import has settled. Idempotent: later calls are no-ops, so the
    /// hosted service can mark from its completion AND its error handler without ordering care.
    /// </summary>
    public void MarkSettled()
    {
        if (settled.IsCompleted)
            return;
        settled.OnNext(Unit.Default);
        settled.OnCompleted();
    }
}
