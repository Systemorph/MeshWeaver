using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Compiler;

/// <summary>
/// The per-type DEPENDENCY RECORD of a compiled NodeType (#1707 slice 2): the sorted
/// <c>(referenced assembly name → surface-id)</c> pairs the emitted assembly actually binds,
/// stamped at compile write-back and validated at ADOPT time — <c>HasUsableBuild</c>, the bake
/// probe's <c>Classify</c>, and the prebuilt seeder all compare the SAME record, so "is this
/// build still valid here" has one answer.
///
/// <para><b>Why the EMITTED refs:</b> Roslyn emits an AssemblyRef row only for assemblies the
/// produced IL/metadata actually uses, so <c>Assembly.GetReferencedAssemblies()</c> on the
/// compiled output is the pruned, true dependency set — no authoring burden, no declared-deps
/// drift. A type that references no module validates on platform surfaces alone and is therefore
/// shareable across every deployment REGARDLESS of what else that deployment installed — the
/// instance-wide <c>InstalledModulesFingerprint</c> stops keying anything for record-stamped
/// builds (it remains only as the legacy rule for records stamped before this shipped).</para>
///
/// <para><b>Surface-id schemes:</b> a platform assembly resolves its REFERENCE-ASSEMBLY hash from
/// the process's surface manifest (<c>ref:&lt;sha&gt;</c> — moves only on a breaking surface
/// change, the same oracle the framework identity uses); an installed module resolves its
/// implementation MVID (<c>mvid:&lt;id&gt;</c> — modules pin by exact build, matching the bundle
/// lane's strict-MVID gate); a platform assembly with no manifest pair falls back to its
/// loaded/on-disk MVID. The scheme prefix keeps the two from ever comparing equal by accident.
/// The reserved <see cref="ToolchainKey"/> entry carries the toolchain's identity
/// (the <see cref="FrameworkBuildIdentity.FullMvidAssemblies"/> closure members' MVIDs): the
/// emitted code never REFERENCES the toolchain, but its generated input was shaped by it, so a
/// record cannot claim validity across a toolchain change.</para>
///
/// <para>Non-MeshWeaver, non-module references (System.*, TPA) are deliberately OUTSIDE the
/// record — they roll with the image/TFM, exactly as they are outside the framework identity
/// (#1696's deliberate narrowing).</para>
/// </summary>
public static class CompiledDependencies
{
    /// <summary>Scheme prefix for a reference-assembly (API surface) hash.</summary>
    public const string RefAsmScheme = "ref:";

    /// <summary>Scheme prefix for an implementation MVID.</summary>
    public const string MvidScheme = "mvid:";

    /// <summary>Recorded when a name is in scope but nothing resolves an id for it — absence is
    /// part of the record, never silently skipped (two environments with different presence sets
    /// must never validate against each other).</summary>
    public const string AbsentId = "absent";

    /// <summary>
    /// The reserved record key carrying the compile toolchain's identity — a '!' prefix so it can
    /// never collide with an assembly simple name.
    /// </summary>
    public const string ToolchainKey = "!toolchain";

    /// <summary>
    /// The reserved record key carrying the build's CONTENT KEY (#1707 slice 4) — the hash of the
    /// FULLY GENERATED compilation input folded with the pruned reference surfaces
    /// (<see cref="GeneratedInputIdentity"/>). Same '!' prefix convention as
    /// <see cref="ToolchainKey"/>.
    ///
    /// <para><b>What it is FOR, and what it is not.</b> <see cref="ToolchainKey"/> is a PROXY —
    /// "the toolchain moved, so the bytes MIGHT be stale". This is the direct observation: "the
    /// input that produced these bytes hashed to X". A consumer that has REGENERATED the input can
    /// therefore answer exactly, and a toolchain change that did not move a type's generated input
    /// is provably not a reason to rebuild it.</para>
    ///
    /// <para>🚨 It is consequently validated ONLY against a live value the caller computed by
    /// regenerating (<see cref="FindMismatch"/>'s <c>liveContentKey</c>). There is no cheap live
    /// counterpart: the key is a function of the toolchain AND the source text, and no metadata-only
    /// check can evaluate it. That is precisely why the toolchain entry cannot simply be deleted in
    /// favour of this one — see the note on <see cref="ComputeToolchainId"/>.</para>
    ///
    /// <para>OPTIONAL: a record produced where the generated input was not available (an adopted
    /// prebuilt, the disk-cache hit path, the hydration shortcut) carries no entry, and
    /// <see cref="FindMismatch"/> then simply has nothing to compare — the toolchain entry still
    /// governs, exactly as before this key existed.</para>
    /// </summary>
    public const string ContentKey = "!input";

    /// <summary>
    /// The toolchain id for <see cref="ToolchainKey"/>: SHA-256 over the
    /// <see cref="FrameworkBuildIdentity.FullMvidAssemblies"/> closure members' implementation
    /// MVIDs (name=mvid lines, in the closure's sorted order), 32 hex chars. Moves exactly when
    /// the generated input of every compile can have moved.
    /// </summary>
    /// <param name="implMvidOf">Resolves an assembly's implementation MVID ("N" format), or null
    /// when not present in this process.</param>
    public static string ComputeToolchainId(Func<string, string?> implMvidOf)
    {
        var text = new StringBuilder();
        foreach (var name in FrameworkBuildIdentity.FullMvidAssemblies)
            text.Append(name).Append('=').Append(implMvidOf(name) ?? AbsentId).Append('\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return MvidScheme + Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>
    /// Builds the record for an emitted assembly: every referenced simple name
    /// <paramref name="idOf"/> declares IN SCOPE (non-null id), plus the reserved
    /// <see cref="ToolchainKey"/> entry. Sorted (ordinal) so the record is deterministic and
    /// serializes canonically.
    /// </summary>
    /// <param name="referencedSimpleNames">The emitted assembly's AssemblyRef simple names
    /// (<c>Assembly.GetReferencedAssemblies()</c> on the compiled output).</param>
    /// <param name="idOf">The surface-id resolver (see <see cref="CreateIdResolver"/>) —
    /// null = out of scope (System/TPA), excluded from the record.</param>
    /// <param name="toolchainId">The producing process's toolchain id
    /// (<see cref="ComputeToolchainId"/>).</param>
    /// <param name="generatedInputDigest">The stage-1 generated-input digest
    /// (<see cref="GeneratedInputIdentity.OfGeneratedInput"/>) of the compile that produced these
    /// bytes, or null when the caller did not compile (an adopted prebuilt, a cache hit, the
    /// hydration shortcut). Non-null folds the pruned reference surfaces in and writes the
    /// <see cref="ContentKey"/> entry — the record's own assembly entries ARE that set, so the two
    /// cannot drift apart.</param>
    public static ImmutableSortedDictionary<string, string> Compute(
        IEnumerable<string?> referencedSimpleNames,
        Func<string, string?> idOf,
        string toolchainId,
        string? generatedInputDigest = null)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        builder[ToolchainKey] = toolchainId;
        foreach (var name in referencedSimpleNames)
        {
            if (string.IsNullOrEmpty(name) || builder.ContainsKey(name))
                continue;
            if (idOf(name) is { } id)
                builder[name] = id;
        }
        if (!string.IsNullOrEmpty(generatedInputDigest))
            // The reference surfaces are the record's ASSEMBLY entries — the reserved '!' entries
            // are deliberately excluded. Folding the toolchain MVID into the content key would
            // make the key move on every toolchain commit, which is the exact proxy this key
            // exists to replace.
            builder[ContentKey] = GeneratedInputIdentity.Combine(
                generatedInputDigest, builder.Where(pair => !IsReservedKey(pair.Key)));
        return builder.ToImmutable();
    }

    /// <summary>
    /// Validates a stamped record against the LIVE environment: null when every entry still
    /// resolves the same id, else a one-line description of the FIRST mismatch (for logs and
    /// decline reasons). An entry whose name the live resolver declares out of scope counts as a
    /// mismatch — a build that binds something this environment cannot even classify is not
    /// provably compatible.
    /// </summary>
    /// <param name="record">The stamped record.</param>
    /// <param name="liveIdOf">The live surface-id resolver (<see cref="CreateIdResolver"/>).</param>
    /// <param name="liveToolchainId">The live toolchain id (<see cref="ComputeToolchainId"/>).</param>
    /// <param name="liveContentKey">The content key the caller computed by REGENERATING this
    /// type's compile input (<see cref="GeneratedInputIdentity"/>), or null when it did not — see
    /// <see cref="ContentKey"/>.</param>
    public static string? FindMismatch(
        IReadOnlyDictionary<string, string> record,
        Func<string, string?> liveIdOf,
        string liveToolchainId,
        string? liveContentKey = null)
        => Compare(record, liveIdOf, liveToolchainId, liveContentKey, demoteToolchain: false);

    /// <summary>
    /// 🚨 <see cref="FindMismatch"/> with the toolchain entry DEMOTED from an invalidation unit to
    /// a trigger (#1976) — the read half of the content key, and the only place the demotion is
    /// expressed.
    ///
    /// <para><b>The rule.</b> When <paramref name="liveContentKey"/> is supplied AND equals the
    /// record's stamped <see cref="ContentKey"/>, the reserved <see cref="ToolchainKey"/> entry
    /// stops deciding: the proxy it stands in for ("the toolchain moved, so the generated input
    /// MIGHT have moved") has been answered directly, and the answer is no. Every other entry
    /// still decides — the assembly <c>ref:</c>/<c>mvid:</c> pairs are compared exactly as before,
    /// so a real surface or module drift is still a mismatch.</para>
    ///
    /// <para><b>Why the demotion cannot silently widen.</b> The live key a caller may pass is
    /// formed by <see cref="LiveContentKeyOf"/>, which folds the LIVE resolution of the record's
    /// OWN assembly entries into the hash. So "the content key matched" already implies "every
    /// stamped assembly entry still resolves identically" — the loop below re-checks them anyway,
    /// deliberately, so the demotion stays confined to one entry even if the key's shape ever
    /// changes.</para>
    ///
    /// <para>🚨 <b>Absence is never equality.</b> A null/empty live key, or a record carrying no
    /// <see cref="ContentKey"/>, falls through to EXACTLY <see cref="FindMismatch"/>'s behaviour:
    /// the toolchain entry stays decisive and the type rebuilds. A false MISMATCH costs one
    /// rebuild; a false MATCH serves stale bytes (#2813), so every inconclusive case takes the
    /// rebuild side.</para>
    /// </summary>
    /// <param name="record">The stamped record.</param>
    /// <param name="liveIdOf">The live surface-id resolver (<see cref="CreateIdResolver"/>).</param>
    /// <param name="liveToolchainId">The live toolchain id (<see cref="ComputeToolchainId"/>).</param>
    /// <param name="liveContentKey">The content key the caller computed by REGENERATING this
    /// type's compile input — <see cref="LiveContentKeyOf"/>. Null when it did not.</param>
    public static string? FindMismatchAfterReevaluation(
        IReadOnlyDictionary<string, string> record,
        Func<string, string?> liveIdOf,
        string liveToolchainId,
        string? liveContentKey)
    {
        ArgumentNullException.ThrowIfNull(record);
        var proven =
            !string.IsNullOrEmpty(liveContentKey)
            && record.TryGetValue(ContentKey, out var stamped)
            && string.Equals(stamped, liveContentKey, StringComparison.Ordinal);
        return Compare(record, liveIdOf, liveToolchainId, liveContentKey, demoteToolchain: proven);
    }

    private static string? Compare(
        IReadOnlyDictionary<string, string> record,
        Func<string, string?> liveIdOf,
        string liveToolchainId,
        string? liveContentKey,
        bool demoteToolchain)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(liveIdOf);

        // 🚨 A record WITHOUT the reserved toolchain entry is never trusted: FindMismatch only
        // compares entries that are PRESENT, so such a record would validate forever across
        // toolchain changes — fail-open. Compute always writes the key, so a legitimate record
        // always carries it; anything else (an empty or hand-assembled record) declines and the
        // type compiles, the always-safe direction.
        if (!record.ContainsKey(ToolchainKey))
            return $"the record carries no '{ToolchainKey}' entry, so it cannot invalidate on "
                + "toolchain changes and is not trusted";

        foreach (var (name, stamped) in record)
        {
            string live;
            if (string.Equals(name, ToolchainKey, StringComparison.Ordinal))
            {
                // 🚨 THE DEMOTION (#1976). Reached only from
                // FindMismatchAfterReevaluation, and only when the regenerated content key
                // PROVED the generated input unchanged. Everywhere else the toolchain entry is
                // decisive, exactly as it has always been.
                if (demoteToolchain)
                    continue;
                live = liveToolchainId;
            }
            else if (string.Equals(name, ContentKey, StringComparison.Ordinal))
            {
                // The content key has NO cheap live counterpart — evaluating it means regenerating
                // the compile input. A caller that did so passes it and gets an EXACT verdict; a
                // caller that did not skips the entry rather than inventing a live value, and the
                // toolchain entry above still governs. Skipping is not fail-open here for exactly
                // that reason: the record is refused outright if it carries no toolchain entry.
                if (string.IsNullOrEmpty(liveContentKey))
                    continue;
                live = liveContentKey;
            }
            else
                live = liveIdOf(name) ?? AbsentId;
            if (!string.Equals(stamped, live, StringComparison.Ordinal))
                return $"'{name}' built against {stamped}, live is {live}";
        }
        return null;
    }

    /// <summary>
    /// 🚨 THE ONE WAY a non-compiling consumer forms the live content key for a stamped record
    /// (#1976) — <see cref="GeneratedInputIdentity.Combine"/> of the caller's freshly REGENERATED
    /// stage-1 digest with the LIVE resolution of the record's own assembly entries.
    ///
    /// <para><b>Why the record's own entry set.</b> Stage 2 folds the PRUNED reference surfaces
    /// read off the emitted assembly, and a caller that has not compiled cannot know that set. The
    /// record's assembly entries ARE that set, as recorded by the producer, so resolving exactly
    /// those names against this environment reproduces the stamped key when — and only when —
    /// both halves are unchanged. Equality therefore proves two things at once: the generated
    /// input is byte-identical, and every assembly the build binds still presents the same
    /// surface here.</para>
    ///
    /// <para>Returns <c>null</c> — never a value — when the record carries no
    /// <see cref="ContentKey"/> (there is nothing to compare against) or when the caller supplied
    /// no digest (nothing was regenerated). A null result is INCONCLUSIVE, and every consumer must
    /// treat it as "rebuild", never as "match".</para>
    /// </summary>
    /// <param name="record">The stamped record.</param>
    /// <param name="liveIdOf">The live surface-id resolver (<see cref="CreateIdResolver"/>).</param>
    /// <param name="liveGeneratedInputDigest">The stage-1 digest
    /// (<see cref="GeneratedInputIdentity.OfGeneratedInput"/>) of the compile input as REGENERATED
    /// now, or null when the caller did not regenerate.</param>
    public static string? LiveContentKeyOf(
        IReadOnlyDictionary<string, string> record,
        Func<string, string?> liveIdOf,
        string? liveGeneratedInputDigest)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(liveIdOf);
        if (string.IsNullOrEmpty(liveGeneratedInputDigest) || !record.ContainsKey(ContentKey))
            return null;
        return GeneratedInputIdentity.Combine(
            liveGeneratedInputDigest,
            record.Where(pair => !IsReservedKey(pair.Key))
                .Select(pair => new KeyValuePair<string, string>(
                    pair.Key, liveIdOf(pair.Key) ?? AbsentId)));
    }

    /// <summary>
    /// 🚨 THE RESTAMP (#1976): the record with its reserved <see cref="ToolchainKey"/> entry moved
    /// to the live value, and NOTHING else touched.
    ///
    /// <para><b>Legitimate only after a <c>CarryForward</c> verdict</b>
    /// (<see cref="ContentKeyReevaluation.Reevaluate"/>). The toolchain entry is a PROXY for "the
    /// generated input might have moved"; a carry-forward is the direct observation that it did
    /// not. Restamping is what makes that observation DURABLE — every metadata-only reader
    /// (<c>HasUsableBuild</c>, the bake probe, the prebuilt seeder) then answers correctly without
    /// regenerating anything, and the next toolchain move re-arms the trigger exactly as before.
    /// </para>
    ///
    /// <para>The <see cref="ContentKey"/> and every assembly entry are carried through untouched:
    /// they are the evidence, and a restamp that rewrote them would be asserting something nobody
    /// measured.</para>
    /// </summary>
    public static ImmutableSortedDictionary<string, string> RestampToolchain(
        ImmutableSortedDictionary<string, string> record, string liveToolchainId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(liveToolchainId);
        return record.SetItem(ToolchainKey, liveToolchainId);
    }

    /// <summary>True for the reserved '!'-prefixed record keys, which name compile FACTS rather
    /// than referenced assemblies.</summary>
    public static bool IsReservedKey(string key) =>
        key.StartsWith('!');

    /// <summary>
    /// The surface-id resolver over one environment: installed modules FIRST (exact-build MVID —
    /// a module name wins even when it starts with <c>MeshWeaver.</c>, because modules pin by
    /// build), then platform assemblies (<c>MeshWeaver.*</c>: manifest ref-asm hash, else
    /// loaded/on-disk MVID, else <see cref="AbsentId"/>), everything else out of scope (null).
    /// Producer and consumer build their resolver from THEIR OWN environment; the ref-asm scheme
    /// is what makes the ids equal across hosts whose surfaces are equal (#1696's construction).
    /// </summary>
    /// <param name="surfaceByName">The process's surface-manifest pairs
    /// (<see cref="FrameworkBuildIdentity.ProcessSurfacePairs"/>; empty for manifest-less
    /// hosts).</param>
    /// <param name="moduleMvidByName">Installed module assembly simple name → MVID ("N").</param>
    /// <param name="implMvidOf">Fallback MVID resolution for manifest-less platform assemblies
    /// (loaded assembly or a metadata-only read beside the app), or null when absent.</param>
    public static Func<string, string?> CreateIdResolver(
        IReadOnlyDictionary<string, string> surfaceByName,
        IReadOnlyDictionary<string, string> moduleMvidByName,
        Func<string, string?> implMvidOf)
        => name =>
        {
            if (moduleMvidByName.TryGetValue(name, out var moduleMvid))
                return MvidScheme + moduleMvid;
            if (!name.StartsWith("MeshWeaver.", StringComparison.Ordinal))
                return null;
            if (surfaceByName.TryGetValue(name, out var surface))
                return RefAsmScheme + surface;
            return implMvidOf(name) is { } mvid ? MvidScheme + mvid : AbsentId;
        };
}
