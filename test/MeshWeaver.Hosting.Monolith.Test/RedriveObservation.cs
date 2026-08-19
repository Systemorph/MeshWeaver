using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

using MeshWeaver.Compiler;
namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 The observation vocabulary for the automatic-re-drive tests (<see
/// cref="NeverCompiledFailureRedriveTest"/>), factored out for one reason: a NEGATIVE assertion
/// cannot be trusted on the word of the test that uses it. These predicates are fed a CONSTRUCTED
/// replay by <c>RedriveObservationReplayTest</c>, which pins both halves — the old shape accepting
/// it, and this one rejecting it while still accepting the genuine case.
///
/// <para><b>The defect this exists to close.</b> <c>workspace.GetMeshNodeStream(path)</c> is a
/// <c>Replay(1)</c> mirror (<c>MeshNodeStreamCache</c> — <c>new ReplaySubject&lt;MeshNode&gt;(1)</c>
/// per entry). Two consequences, and every re-drive assertion trips over one of them:</para>
/// <list type="number">
///   <item><description><b>A new subscription is handed the snapshot the mirror ALREADY holds.</b>
///     So a bound of the form "wait for <c>Version &gt;= X</c>" where <c>X</c> is a version the
///     mirror already has is satisfied by that hand-off and proves nothing at all. That is exactly
///     what the forge barrier did: <c>GetMeshNodeStream(path).Update(...)</c> is <c>UpdateRemote</c>,
///     which emits <c>update(current)</c> — the OPTIMISTIC LOCAL snapshot, carrying the PRE-write
///     <see cref="MeshNode.Version"/>, because the update lambda only rewrites <c>Content</c>. The
///     barrier therefore waited for a version the stream was already sitting on.</description></item>
///   <item><description><b>Only the LATEST snapshot survives the hand-off.</b> Everything that
///     happened between the moment the caller decided to observe and the moment the pool-scheduled
///     <c>Subscribe</c> attaches is collapsed into one value. A complete re-drive —
///     <c>Pending → Compiling → Error</c> — fits in that gap, and a state-shaped "nothing was
///     driven" assertion then passes having watched nothing at all.</description></item>
/// </list>
///
/// <para><b>🚨 Whose version is the watermark?</b> The OWNER's, and it is a strict LOWER BOUND, not
/// a receipt. <see cref="MeshNode.Version"/> is minted by the owning per-node hub for EVERY writer
/// (<see cref="MeshNode.NextVersion"/> = <c>current + 1</c>) — issue #1833 is a test that flaked on
/// assuming it tracks the caller's own write, and it does not. So <c>Version &gt; watermark</c>
/// claims exactly one thing: the owner has committed at least one revision past the snapshot the
/// caller was looking at, so this emission cannot be that snapshot replayed. It deliberately does
/// NOT claim "my write landed" — that claim belongs to the LEDGER below, which counts EVENTS.</para>
///
/// <para><b>The ledger is the PRIMARY bound.</b> <see cref="NodeTypeCompileParkRegistry"/> counts
/// what actually happened in this process: <c>RecordFailureRedrive</c> on every automatic re-drive
/// kickoff, <c>RecordAttempt</c> on every real Roslyn dispatch (<c>RunCompile</c>). A counter cannot
/// be replayed, cannot be conflated, and — unlike the node's state — can tell "the re-drive ran and
/// re-failed" apart from "I was handed the pre-forge snapshot back", which for a broken type are
/// byte-identical records. The stream observation stays as corroboration, anchored so a replay
/// cannot trip it either way.</para>
/// </summary>
internal static class RedriveObservation
{
    /// <summary>
    /// The forge barrier's bound: has the owner committed a revision STRICTLY past
    /// <paramref name="watermark"/>? Strict, because <c>&gt;=</c> is satisfied by the replayed
    /// snapshot the watermark was read from — the whole defect.
    /// </summary>
    public static bool IsPastWatermark(MeshNode? node, long watermark)
        => node is { } n && n.Version > watermark;

    /// <summary>
    /// A compile drive that is NEW relative to <paramref name="watermark"/> — the emission the
    /// negative assertion forbids. The version clause is what stops the mirror's hand-off of the
    /// snapshot the window OPENED on from reading as a fresh drive.
    /// </summary>
    public static bool IsFreshCompileDrive(MeshNode? node, long watermark)
        => node is { } n
            && n.Version > watermark
            && n.Content is NodeTypeDefinition d
            && d.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling;

    /// <summary>
    /// Opens a "nothing further is driven" window: captures the ledger AND the watermark at the
    /// caller's own point in the timeline, which is the moment the claim is about. Capturing at
    /// SUBSCRIBE time instead — which is what a bare <c>NotEmit</c> does, on a pool thread — is how
    /// a whole re-drive fits between the claim and its evidence.
    /// </summary>
    public static RedriveWindow OpenWindow(
        NodeTypeCompileParkRegistry ledger, string typePath, long watermark)
        => new(ledger, typePath, watermark,
            ledger.GetFailureRedriveCount(typePath), ledger.GetCompileAttemptCount(typePath));
}

/// <summary>
/// An open "nothing further is driven" observation window — see <see cref="RedriveObservation"/>.
/// </summary>
/// <param name="Ledger">The process-wide re-drive / compile-attempt ledger.</param>
/// <param name="TypePath">The NodeType the claim is about.</param>
/// <param name="Watermark">The node version the window opened on.</param>
/// <param name="RedrivesAtOpen">Automatic re-drives recorded when the window opened.</param>
/// <param name="AttemptsAtOpen">Real Roslyn dispatches recorded when the window opened.</param>
internal sealed record RedriveWindow(
    NodeTypeCompileParkRegistry Ledger,
    string TypePath,
    long Watermark,
    int RedrivesAtOpen,
    int AttemptsAtOpen)
{
    /// <summary>
    /// Closes the window and asserts nothing was driven inside it. Two independent witnesses,
    /// because neither alone is sound: the LEDGER (primary — events, unreplayable, uncollapsible)
    /// and the anchored stream observation (corroboration — catches a drive that reached the node
    /// but somehow never reached the ledger).
    /// </summary>
    /// <returns>A one-line summary of what the window observed, for the test's output.</returns>
    public async Task<string> AssertNothingWasDriven(
        IObservable<MeshNode> stream, TimeSpan window, string because)
    {
        await stream
            .Where(n => RedriveObservation.IsFreshCompileDrive(n, Watermark))
            .Should().NotEmit(window, because);

        var redrives = Ledger.GetFailureRedriveCount(TypePath);
        var attempts = Ledger.GetCompileAttemptCount(TypePath);
        redrives.Should().Be(RedrivesAtOpen,
            $"{because} — the automatic re-drive ledger for {TypePath} must not move inside the "
            + "window, and it is the PRIMARY bound: a re-drive that started and re-settled between "
            + "the claim and the mirror's Replay(1) hand-off leaves the node in a state that is "
            + "byte-identical to the one the window opened on, so only a counter can see it");
        attempts.Should().Be(AttemptsAtOpen,
            $"{because} — and no real Roslyn dispatch (NodeTypeCompilationHelpers.RunCompile → "
            + $"RecordAttempt) may happen for {TypePath} inside the window either");

        return $"nothing driven for {TypePath} in {window.TotalSeconds:F0}s "
            + $"(watermark v{Watermark}; re-drives {redrives}, attempts {attempts} — both unchanged)";
    }
}
