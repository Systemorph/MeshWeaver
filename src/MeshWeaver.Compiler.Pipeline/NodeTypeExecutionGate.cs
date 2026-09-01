using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// What a NodeType's <see cref="NodeTypeDefinition.BuildProvenance"/> says about whether its
/// compiled bytes may be given a DURABLE, write-capable home — the execute-time half of #2813
/// (issue #2820).
///
/// <para>🚨 THREE values, never two. #2901's lesson, and #890's before it: a probe that cannot
/// reach a verdict must say so rather than answer its scariest branch (or its friendliest one) on
/// its own inability to run. A boolean here would force the caller to pick, and both choices are
/// wrong — <see cref="Permitted"/> is a silent fail-open, <see cref="Refused"/> would park a type
/// on a read that merely timed out.</para>
/// </summary>
public enum BuildExecutionVerdict
{
    /// <summary>
    /// The build may be armed. Covers <see cref="BuildProvenance.Compiled"/> (Roslyn built these
    /// bytes here), <see cref="BuildProvenance.AdoptedVerified"/> (the bundle's fingerprint matched
    /// the live source) — and, deliberately, <see cref="BuildProvenance.AdoptedUnverified"/>.
    /// </summary>
    Permitted,

    /// <summary>
    /// 🚨 The bytes were PROVEN stale: the bundle records which sources it was built from and they
    /// are not the ones this mesh holds. This is the one hard refusal, and the case the whole
    /// mechanism exists for.
    /// </summary>
    Refused,

    /// <summary>
    /// No verdict. The definition could not be read at all, so nothing was compared — neither
    /// verified nor refused. Retryable by construction (the next read re-evaluates), and never
    /// collapsible into either of the other two.
    /// </summary>
    Inconclusive
}

/// <summary>
/// THE execute-time interlock: a control plane must refuse to arm a type whose build provenance is
/// PROVEN stale (Systemorph/MeshWeaver#2820). #2813 made the provenance visible and refused a
/// provably-stale adoption at LOAD time; this is the first line of defence, not the second —
/// "the damage needed two ingredients: stale bytes, and something armed to run them."
///
/// <para><b>What it refuses, and the far more important question of what it does NOT.</b>
/// The predicate is a single equality against <see cref="BuildProvenance.AdoptionRefused"/>.
/// Everything else is <see cref="BuildExecutionVerdict.Permitted"/>, and that is not an oversight:
/// </para>
///
/// <list type="bullet">
///   <item>🚨 <b><see cref="BuildProvenance.AdoptedUnverified"/> is PERMITTED.</b> A bundle
///     published before producers recorded a source fingerprint carries none, so its provenance is
///     <i>unknown</i>, not <i>proven stale</i>. Refusing it would park every legacy-bundle type on
///     every mesh — and on a <c>Modules:RequirePrebuilt</c> mesh a local compile is refused by
///     design, so there would be no recovery at all. That is the outage refusing unproven bundles
///     was rejected to avoid, arriving through a different door. The same reasoning that makes
///     <c>ApplyAdoptedSourceStamp</c> KEEP the stamp on the legacy row applies verbatim here.</item>
///   <item><b><see cref="BuildProvenance.Compiled"/> is permitted</b> — Roslyn built the bytes from
///     the source this mesh holds. It is also the zero value, so a record written before the field
///     existed reads as the honest default and no historical node is refused.</item>
///   <item><b><see cref="BuildExecutionVerdict.Inconclusive"/> does not execute either</b>, but it
///     is a different answer and the call sites treat it differently: it is reached only when the
///     definition could not be read at all, where every arming site in the framework already binds
///     NO assembly (the default node chain instead), so the gate names the state rather than
///     inventing a refusal for it. See the call-site notes on
///     <c>NodeTypeEnrichmentHelpers.ApplyStreamResult</c> and
///     <c>CellSurfaceAssemblyProvider.ResolveOne</c>.</item>
/// </list>
///
/// <para><b>Why the signal is trustworthy.</b> <c>ApplyCompileSuccess</c> resets
/// <see cref="BuildProvenance"/> to <see cref="BuildProvenance.Compiled"/>, so a type that was
/// refused and then successfully recompiled is permitted again on the next read — without that
/// reset the field was write-once-per-adoption and this gate would refuse a type whose live source
/// it had just compiled itself. <c>ApplyCompileFailure</c> deliberately does not reset it: after a
/// failed compile the bytes in place are still whatever the refusal left.</para>
///
/// <para>Pure and hub-free so every row is unit-testable with no mesh and no timing, and so the two
/// enforcement sites cannot drift about what "refused" means.</para>
/// </summary>
public static class NodeTypeExecutionGate
{
    /// <summary>
    /// The verdict for one NodeType definition. <c>null</c> in —
    /// <see cref="BuildExecutionVerdict.Inconclusive"/> out; the definition could not be read, so
    /// nothing was compared.
    /// </summary>
    public static BuildExecutionVerdict Evaluate(NodeTypeDefinition? definition)
        => definition is null
            ? BuildExecutionVerdict.Inconclusive
            : definition.BuildProvenance switch
            {
                BuildProvenance.AdoptionRefused => BuildExecutionVerdict.Refused,
                // Compiled / AdoptedVerified / AdoptedUnverified. Listed as the default arm ON
                // PURPOSE: BuildProvenance is append-only, and a member added later must not
                // silently become a refusal — a new provenance nobody has taught this gate about
                // is by definition not PROVEN stale. Refusal is opt-in, one name at a time.
                _ => BuildExecutionVerdict.Permitted,
            };

    /// <summary>Convenience for the call sites that only need the hard refusal.</summary>
    public static bool RefusesExecution(NodeTypeDefinition? definition)
        => Evaluate(definition) is BuildExecutionVerdict.Refused;

    /// <summary>
    /// The bundle's recorded source fingerprint and this mesh's live one, shortened to the first
    /// twelve characters — enough to identify a build, short enough for a log line and a page.
    /// The PAIR is what makes a refusal checkable against the bundle by hand, so every surface
    /// that reports one carries both.
    ///
    /// <para>Separated from <see cref="RefusalSummary"/> so a user-visible surface can format its
    /// OWN localized sentence around these three facts instead of rendering an English one: the
    /// refusal page reads <c>ui.executionRefusedSummary</c> with exactly these arguments.</para>
    /// </summary>
    public static (string Adopted, string Live) Fingerprints(NodeTypeDefinition definition)
        => (Short(definition.AdoptedSourceFingerprint), Short(definition.CurrentSourceFingerprint));

    /// <summary>
    /// The refusal as ONE English sentence, for the LOG line and the delivery NACK — the two
    /// machine/operator-facing surfaces, which stay English by house rule (log lines ship to Loki;
    /// a NACK reason is read by developers and tools, not rendered to a viewer).
    ///
    /// <para>🚨 NOT for the page. What a viewer reads is built at render time from
    /// <c>ui.executionRefusedSummary</c> plus <see cref="Fingerprints"/>, so a German reader gets a
    /// German sentence. Passing this string into an overlay would be the hard-coded-UI-string bug.</para>
    /// </summary>
    public static string RefusalSummary(string nodeTypePath, NodeTypeDefinition definition)
        => $"Execution refused for NodeType '{nodeTypePath}': its adopted build was PROVEN stale "
           + $"(bundle source fingerprint '{Short(definition.AdoptedSourceFingerprint)}' vs live "
           + $"'{Short(definition.CurrentSourceFingerprint)}'), so these bytes are not built from "
           + "the source this mesh holds (#2813/#2820).";

    /// <summary>
    /// The recovery verb, stated wherever the refusal is. #2818 cost an hour because a discarded
    /// force reported as a TIMEOUT — a refusal that does not say what to do next repeats that.
    /// </summary>
    public const string RecoveryVerb =
        "Recompile this type (the Recompile button, or the `compile` verb over MCP — a FORCED "
        + "release compiles the live source instead of re-adopting the bundle, #2818). On a "
        + "`Modules:RequirePrebuilt` mesh no local compile is possible: rebake and republish the "
        + "package, then request a release.";

    private static string Short(string? fingerprint)
        => fingerprint is { Length: > 12 } f ? f[..12] : fingerprint ?? "(none)";
}
