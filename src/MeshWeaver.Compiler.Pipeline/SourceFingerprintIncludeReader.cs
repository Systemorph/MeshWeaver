using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The mesh-actor half of the <c>@@</c>-include walk that
/// <see cref="NodeTypeSourceFingerprint"/> runs (#2948) — the System-impersonated, bounded read
/// <see cref="NodeCompileShaping.CollectIncludeClosure"/> is handed so it can find out what an
/// included-only snippet says TODAY.
///
/// <para><b>Why this is not simply <c>MeshNodeCompilationService.ReadIncludeNode</c>.</b> The
/// anchoring, the anchored-then-authored fallback ORDER and the cycle brake are shared — they live
/// in <see cref="NodeCompileShaping"/> and neither reader gets to have an opinion about them. What
/// differs is what a read that could not be COMPLETED means, and it differs in opposite
/// directions:</para>
/// <list type="bullet">
///   <item><b>The compile</b> degrades a stall to "unresolved": the directive stays VERBATIM, Roslyn
///     reports on the <c>@@</c> line, the type parks with a diagnostic a human can read. Turning a
///     transient stall into a hard fault there would park the type on the ACTIVATION path, which is
///     worse than a compile error you can see.</item>
///   <item><b>The fingerprint</b> must NOT degrade it. A stalled read that reads as "absent"
///     shortens the include closure, the hash then differs from the producer's, and the adoption is
///     REFUSED — a false refusal, which on a <c>Modules:RequirePrebuilt</c> mesh is terminal and
///     needs a human to rebake. The honest answer is INCONCLUSIVE: this reader faults, the caller
///     leaves the previous fingerprint standing, and the adoption judgement degrades to
///     <c>AdoptedUnverified</c> — the branch that exists precisely for "nothing has been compared".
///     Same rule as the emit canary (#890): a probe must not answer its scariest branch on its own
///     inability to run.</item>
/// </list>
///
/// <para>🚨 <b>ABSENT is not a failure and must keep being an answer.</b> An <c>@@</c> match that
/// resolves to nothing is ordinary — the scanner runs over raw C# and a path-shaped fragment that
/// names no node is a legitimate outcome, and an include the mesh genuinely does not hold
/// contributes nothing to the bytes either. Both producer and consumer therefore record nothing
/// for it and agree. Only <see cref="NodeReadStatus.Unavailable"/> /
/// <see cref="NodeReadStatus.DeleteInProgress"/> and a timeout are inconclusive.</para>
/// </summary>
public static class SourceFingerprintIncludeReader
{
    /// <summary>The per-read budget — the same 15s the compile path spends on an include, so a
    /// fingerprint can never be the slower of the two on the same mesh.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds the reader for <paramref name="hub"/>: anchored path first, authored path as the
    /// fallback only when the anchored one is genuinely ABSENT, reporting the path that actually
    /// produced the node so nested includes anchor there.
    ///
    /// <para>🚨 <c>RunAsSystem</c>, never <c>Observable.Using(access.ImpersonateAsSystem, …)</c>.
    /// Impersonation is an <c>AsyncLocal</c> store/restore pair and Rx disposes <c>Using</c>'s
    /// resource when the inner observable TERMINATES — a different thread for a cross-hub read —
    /// leaving the subscriber latched as System (#1790). Each read is wrapped INDIVIDUALLY because
    /// the fallback read is subscribed from the first read's emission, i.e. on another thread
    /// again, so one outer scope would not cover it.</para>
    ///
    /// <para>Source-set discovery is framework infrastructure, not a user-scoped read: a per-user
    /// read of a source node UNDER this NodeType routes a permission check BACK into the grain
    /// being read, which deadlocks a single-threaded, non-reentrant activation (#1253).</para>
    /// </summary>
    /// <param name="hub">The reading hub.</param>
    /// <param name="logger">Diagnostics.</param>
    public static Func<string, string?, IObservable<(MeshNode? Node, string Path)>> For(
        IMessageHub hub, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var access = hub.ServiceProvider.GetService<AccessService>();

        return (path, fallbackPath) => Read(hub, access, path, logger)
            .SelectMany(outcome => outcome.Status switch
            {
                NodeReadStatus.Present => Hit(outcome.Node, path),

                // Genuinely absent, and there is another legal reading of the authored path.
                NodeReadStatus.Absent when fallbackPath is { Length: > 0 } =>
                    Read(hub, access, fallbackPath, logger)
                        .SelectMany(fallback => fallback.Status switch
                        {
                            NodeReadStatus.Present => Hit(fallback.Node, fallbackPath),
                            NodeReadStatus.Absent => Hit(null, fallbackPath),
                            _ => Miss(fallbackPath, fallback.Failure),
                        }),

                NodeReadStatus.Absent => Hit(null, path),

                _ => Miss(path, outcome.Failure),
            });
    }

    /// <summary>An established answer: the node, or a well-founded "there is nothing there".</summary>
    private static IObservable<(MeshNode? Node, string Path)> Hit(MeshNode? node, string path) =>
        Observable.Return<(MeshNode? Node, string Path)>((node, path));

    /// <summary>An answer that was never established — the fingerprint is inconclusive.</summary>
    private static IObservable<(MeshNode? Node, string Path)> Miss(string path, Exception? cause) =>
        Observable.Throw<(MeshNode? Node, string Path)>(Inconclusive(path, cause));

    /// <summary>
    /// One System-impersonated read. <see cref="ReadTimeoutBehavior.EmitNull"/> — deliberately, and
    /// it is NOT the "treat indeterminate as absent" contract that flag usually expresses: it maps a
    /// timeout to <see cref="NodeReadStatus.Unavailable"/> carrying the
    /// <see cref="TimeoutException"/>, which is exactly the third state this reader needs. Under
    /// <see cref="ReadTimeoutBehavior.Throw"/> the exception would arrive out of band and could not
    /// be told apart from a routing fault by the caller of <see cref="For"/>.
    /// </summary>
    private static IObservable<NodeReadOutcome> Read(
        IMessageHub hub, AccessService? access, string path, ILogger? logger) =>
        access.RunAsSystem(() =>
            hub.GetMeshNodeOutcome(path, ReadBudget, ReadTimeoutBehavior.EmitNull))
            .Do(outcome =>
            {
                if (outcome.Status is NodeReadStatus.Present)
                    logger?.LogDebug("Fingerprint include read resolved {Path}", path);
            });

    /// <summary>
    /// The one exception shape a caller catches to mean "I could not establish the include closure".
    /// Named so the sources watcher can distinguish it from a genuine defect and skip the
    /// fingerprint publication instead of tearing its subscription down.
    /// </summary>
    /// <param name="path">The include target that could not be established.</param>
    /// <param name="cause">What the read reported, when it reported anything.</param>
    public static SourceIncludeUnavailableException Inconclusive(string path, Exception? cause) =>
        new(path, cause);
}

/// <summary>
/// Raised when an <c>@@</c>-include target could not be READ — as distinct from being absent
/// (#2948). It means the source fingerprint is INCONCLUSIVE for this NodeType right now, never
/// that the include is gone.
/// </summary>
public sealed class SourceIncludeUnavailableException : Exception
{
    /// <summary>The include target whose read could not be completed.</summary>
    public string IncludePath { get; }

    /// <summary>Builds the exception.</summary>
    /// <param name="includePath">The include target whose read could not be completed.</param>
    /// <param name="cause">The underlying read failure, when the read reported one.</param>
    public SourceIncludeUnavailableException(string includePath, Exception? cause = null)
        : base(
            $"The @@ include '{includePath}' could not be READ (it is not known to be absent), so "
            + "the NodeType's source fingerprint is INCONCLUSIVE. Treating an unreadable include as "
            + "absent would shorten the hashed set and turn a good prebuilt bundle into a refused "
            + "adoption — the previous fingerprint stands instead.",
            cause)
        => IncludePath = includePath;
}
