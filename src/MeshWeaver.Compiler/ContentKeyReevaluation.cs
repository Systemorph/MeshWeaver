namespace MeshWeaver.Compiler;

/// <summary>What a re-evaluation concluded about a stamped build — see
/// <see cref="ContentKeyReevaluation"/>.</summary>
public enum ReevaluationVerdict
{
    /// <summary>
    /// 🚨 The comparison could not be made — no record, no <see cref="CompiledDependencies.ContentKey"/>
    /// entry, no live resolver, or the input could not be regenerated. The DEFAULT, deliberately:
    /// an absent answer must never read as "unchanged", so a consumer treats this exactly as it
    /// behaved before the lane existed (the toolchain entry stays decisive).
    /// </summary>
    Inconclusive,

    /// <summary>
    /// The regenerated compile input hashes to the stamped content key and every assembly the
    /// build binds still resolves identically: the existing build is provably interchangeable with
    /// what a compile would produce now. The toolchain entry may be RESTAMPED and no compile is
    /// needed.
    /// </summary>
    CarryForward,

    /// <summary>
    /// Something the emitted bytes depend on has moved — the generated input, or one of the
    /// assemblies the build binds. Compile.
    /// </summary>
    Rebuild,
}

/// <summary>One re-evaluation's verdict plus the sentence that explains it, for logs.</summary>
/// <param name="Verdict">What was concluded.</param>
/// <param name="Detail">A one-line, operator-readable reason. Never null.</param>
public readonly record struct Reevaluation(ReevaluationVerdict Verdict, string Detail);

/// <summary>
/// 🚨 THE RE-EVALUATION LANE's decision function (#1976) — the pure half of "on a toolchain change,
/// regenerate the input and compare; equal ⇒ carry the build forward and restamp, different ⇒
/// compile".
///
/// <para><b>What it demotes, and what it does NOT.</b> Before this existed, the toolchain's
/// implementation MVID was an INVALIDATION UNIT: the reserved
/// <see cref="CompiledDependencies.ToolchainKey"/> entry moves on every body-only commit to any
/// member of the <see cref="FrameworkBuildIdentity.FullMvidAssemblies"/> closure (16 assemblies,
/// 383 commits/30d as measured on #1976), and every such move invalidated every stamped build
/// whether or not that build's compile input had actually changed. The MVID is a PROXY for "the
/// generated input might have moved"; <see cref="CompiledDependencies.ContentKey"/> is the direct
/// observation. Once the direct observation is available the proxy becomes a TRIGGER — a cheap
/// signal that says "go and look" — rather than the answer itself.</para>
///
/// <para>🚨 <b>It does not demote the FRAMEWORK VERSION, and that is deliberate.</b> A build's
/// bytes are addressed in the assembly store under a key carrying
/// <see cref="FrameworkBuildIdentity.FrameworkVersion"/>'s first eight characters, so after a
/// framework roll the PREVIOUS generation's bytes exist on the volume but are unaddressable
/// (<c>FileSystemAssemblyStore</c> globs the LIVE tag). Carrying a build across that boundary on
/// the strength of the content key is a cross-generation assembly load — the failure mode that
/// wedged production on 2026-06-20 — and it is a maintainer scope call, recorded as such on #1976
/// rather than taken here. The lane therefore acts where the bytes are ALREADY addressable under
/// the live tag, and the framework-version check upstream of it is untouched.</para>
///
/// <para><b>Pure, like <c>NodeTypeBakeStatus.Classify</c>:</b> a function over a record, two
/// resolvers and a string. Regenerating the input is the caller's job (it is a mesh read and a
/// hash); deciding what the answer means is here, so every state is unit-testable without a
/// fixture.</para>
/// </summary>
public static class ContentKeyReevaluation
{
    /// <summary>
    /// Decide whether the build described by <paramref name="record"/> can be carried forward.
    /// </summary>
    /// <param name="record">The build's stamped dependency record
    /// (<c>NodeTypeDefinition.CompiledDependencies</c>), or null when none was stamped.</param>
    /// <param name="liveIdOf">The live surface-id resolver
    /// (<see cref="CompiledDependencies.CreateIdResolver"/>), or null when this environment cannot
    /// resolve one — which is itself inconclusive.</param>
    /// <param name="liveToolchainId">The live toolchain id
    /// (<see cref="CompiledDependencies.ComputeToolchainId"/>).</param>
    /// <param name="liveGeneratedInputDigest">The stage-1 digest of the compile input as
    /// REGENERATED now (<see cref="GeneratedInputIdentity.OfGeneratedInput"/>), or null when the
    /// caller could not regenerate it.</param>
    public static Reevaluation Reevaluate(
        IReadOnlyDictionary<string, string>? record,
        Func<string, string?>? liveIdOf,
        string liveToolchainId,
        string? liveGeneratedInputDigest)
    {
        if (record is null || record.Count == 0)
            return new(ReevaluationVerdict.Inconclusive,
                "no dependency record is stamped on this build, so there is nothing to compare");
        if (liveIdOf is null)
            return new(ReevaluationVerdict.Inconclusive,
                "this environment resolves no surface ids, so the record cannot be evaluated");
        if (!record.ContainsKey(CompiledDependencies.ToolchainKey))
            return new(ReevaluationVerdict.Inconclusive,
                $"the record carries no '{CompiledDependencies.ToolchainKey}' entry, so it is not "
                + "trusted at all");
        if (!record.TryGetValue(CompiledDependencies.ContentKey, out var stampedContentKey)
            || string.IsNullOrEmpty(stampedContentKey))
            return new(ReevaluationVerdict.Inconclusive,
                $"the record carries no '{CompiledDependencies.ContentKey}' entry — it was stamped "
                + "by a producer that had no generated input in hand (an adopted prebuilt, a cache "
                + "hit) or predates the key");
        if (string.IsNullOrEmpty(liveGeneratedInputDigest))
            return new(ReevaluationVerdict.Inconclusive,
                "the compile input was not regenerated, so the content key has no live counterpart");

        var liveContentKey =
            CompiledDependencies.LiveContentKeyOf(record, liveIdOf, liveGeneratedInputDigest);
        if (string.IsNullOrEmpty(liveContentKey))
            return new(ReevaluationVerdict.Inconclusive,
                "the live content key could not be formed from the record");

        if (!string.Equals(stampedContentKey, liveContentKey, StringComparison.Ordinal))
            // 🚨 The under-invalidation side, and the reason this is not merely an optimisation:
            // a change that moves the GENERATED INPUT while leaving every metadata-only signal
            // constant (a generator body edit, an option flip, an `@@`-included snippet the
            // source-version snapshot never records) is invisible to every other check in the
            // framework. Here it is decisive.
            return new(ReevaluationVerdict.Rebuild,
                $"'{CompiledDependencies.ContentKey}' built against {stampedContentKey}, "
                + $"live is {liveContentKey}");

        // The content key matched, so the toolchain entry demotes. Everything else still decides —
        // belt and braces, since LiveContentKeyOf already folded the live resolution of exactly
        // these names into the key it just matched.
        if (CompiledDependencies.FindMismatchAfterReevaluation(
                record, liveIdOf, liveToolchainId, liveContentKey) is { } mismatch)
            return new(ReevaluationVerdict.Rebuild, mismatch);

        return new(ReevaluationVerdict.CarryForward,
            $"the regenerated compile input still hashes to {liveContentKey} — the toolchain moved "
            + "but this type's generated input did not");
    }
}
