using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// THE adopt-time framework-identity gate: may THIS process load the build a NodeType's record
/// names, and what status may it honestly report for that record — Systemorph/MeshWeaver#3472.
///
/// <para>One predicate, two consumers, deliberately shaped like
/// <c>MeshPublicationGate</c>'s (#3478/#3491): a divergence is what caused the incident, so the
/// serve-time refusal and the reported status are DERIVED from a single comparison rather than
/// being two tables that can drift apart.</para>
///
/// <h3>What happened</h3>
///
/// <para>On 2026-09-06, <c>Crm/Offer</c> and <c>Crm/Opportunity</c> on a client portal read
/// <c>compilationStatus: Ok</c> while every one of their per-instance hubs was dead — one timing
/// out on activation, the other answering "Area not found". Their records named assemblies stamped
/// <c>sc273ee39f…</c>; the replicas serving the portal ran <c>s2f227642d…</c>. Every deal page and
/// every offer page was dead for about two and a half hours, and the NodeType's own self-report was
/// green throughout. <c>Doc/Architecture/MeshAdmission</c> carries the membership half of the chain
/// (a readiness-refused pod that kept publishing); this is the adoption half.</para>
///
/// <h3>Why <c>Ok</c> was a lie, stated precisely</h3>
///
/// <para>🚨 <b><see cref="CompilationStatus.Ok"/> is a claim SCOPED to
/// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>, persisted as though it were
/// absolute.</b> "The last compile succeeded" is true and process-independent; "these bytes can be
/// loaded" is only ever answerable relative to a process. The record has always carried both halves
/// — the verdict and its scope — in two separate fields, and every instrument read the first one.
/// That is the whole defect, and it is why no amount of care at the WRITER can fix it alone: the
/// pod that wrote <c>Ok</c> could load what it had just built.</para>
///
/// <para>So the answer is split, and each half is necessary:</para>
///
/// <list type="number">
///   <item><b>The writer half.</b> A persisted <c>Ok</c> that names an assembly must name the
///     framework identity of the process that persisted it — the identity travels with the
///     coordinates, exactly as <c>NodeTypeContractHandler.ResolvedStoreVersion</c> already argues
///     the store-key VERSION must ("the path and the version are ONE reference, so they must come
///     from ONE source"). The framework identity is the fourth member of that reference and was
///     left out of it: a hydrate carried a foreign stamp forward over bytes the store had just
///     resolved under the LIVE tag, producing a record that says <c>Ok</c>, names loadable bytes,
///     and declares them unloadable. <c>HasUsableBuild</c> is then false forever, every activation
///     takes the ABI-stale recompile path, and after <c>MaxRecompileAttempts</c> the instance binds
///     the fallback configuration for the grain's whole life — which is "Area not found", under a
///     green tick.</item>
///   <item><b>The reader half — this class.</b> Foreign-<c>Ok</c> records still ARRIVE from
///     elsewhere: a peer replica on another image (#3395), a node repo that COMMITS a record (the
///     plugins repo ships <c>Store/Catalog</c> with a July framework hash), a prebuilt bundle. This
///     process must not repeat their <c>Ok</c>, and it must not load their bytes.</item>
/// </list>
///
/// <para>🚨 <b>Nothing here is ever written.</b> <see cref="CompilationStatus.Foreign"/> is derived
/// at read time and persisted by no one. Writing a reader-relative verdict into a shared record is
/// how #3395's ping-pong was made — two replicas, two identities, each correctly overwriting the
/// other's answer forever. That is also what makes this compose with <c>MeshPublicationGate</c>
/// instead of duplicating it: that gate decides WHETHER this process may publish; this one adds
/// nothing to publish.</para>
///
/// <h3>What refusing means</h3>
///
/// <para>🚨 <b>Refusing is not "the type is dead" — the fallback is a LOCAL COMPILE</b>, and every
/// call site already has that path because it is the same one a store MISS takes today. That is not
/// a coincidence: on a store whose key carries the framework tag (which
/// <c>FileSystemAssemblyStore</c>'s <c>v{version}-{FrameworkTag}-*.dll</c> glob does) a
/// foreign-identity record already misses. This gate makes that guarantee EXPLICIT and
/// store-independent, so it no longer rests on an eight-character substring inside one
/// implementation's glob pattern — with a second implementation (<c>BlobAssemblyStore</c>) living
/// in a module this repository cannot compile, and no test anywhere asserting the property.</para>
///
/// <para>Pure and hub-free, so every row is unit-testable with no mesh and no timing, and so the
/// enforcement sites cannot drift about what "foreign" means. Sibling gate on the orthogonal axis
/// (bytes vs SOURCE rather than bytes vs FRAMEWORK): <see cref="NodeTypeExecutionGate"/>.</para>
/// </summary>
public static class NodeTypeBuildIdentity
{
    /// <summary>
    /// Why this process may NOT load the build <paramref name="definition"/> names, or
    /// <c>null</c> when it may — compared against an EXPLICIT live identity so a test can stage a
    /// framework roll without rebuilding the framework (the same seam
    /// <see cref="NodeTypeBakeStatus.Classify"/> and
    /// <c>PrebuiltAssemblySeeder.DeclineReason</c> expose, for the same reason).
    ///
    /// <para>The comparison is deliberately the SAME clause
    /// <c>NodeTypeCompilationHelpers.HasUsableBuild</c> already applies on the one load path that
    /// was guarded — an ordinal equality, with an ABSENT identity refused. Extending it to the
    /// paths that were not guarded is therefore consistency, not a new policy: a record whose
    /// identity is absent cannot be shown ABI-compatible with anything, and the framework has
    /// treated it as unusable since #464.</para>
    ///
    /// <para>🚨 A record that names NO assembly is not refused. There is nothing to load, so there
    /// is nothing to refuse; the caller's own "no build recorded" branch is the right answer and
    /// it already exists at every site. Answering a refusal there would report an identity problem
    /// for a type that simply has not been built.</para>
    /// </summary>
    public static string? RefusalReason(NodeTypeDefinition? definition, string liveFrameworkVersion)
        => RefusalReason(definition, liveFrameworkVersion, NodeTypeCompilationHelpers.LivePlatformVersion);

    /// <summary>
    /// <see cref="RefusalReason(NodeTypeDefinition?, string)"/> with the running platform BUILD
    /// explicit — the platform-range half (policy <c>platform-backwards-compatibility</c>): within
    /// one compatibility key a record whose FLOOR (<see cref="NodeTypeDefinition.CompiledPlatformVersion"/>,
    /// the build that produced it) is NEWER than <paramref name="livePlatformVersion"/>, or whose
    /// CEILING is below it, is refused — both versions named. An absent floor is "unknown producer =
    /// older" and admitted.
    /// </summary>
    /// <param name="definition">The NodeType record.</param>
    /// <param name="liveFrameworkVersion">This process's compatibility key.</param>
    /// <param name="livePlatformVersion">This process's platform build, or null when unknown.</param>
    public static string? RefusalReason(
        NodeTypeDefinition? definition, string liveFrameworkVersion, string? livePlatformVersion)
    {
        if (definition is null)
            return null;
        if (string.IsNullOrEmpty(definition.LatestAssemblyCollection)
            || string.IsNullOrEmpty(definition.LatestAssemblyPath))
            return null;
        if (string.Equals(
                definition.CompiledFrameworkVersion, liveFrameworkVersion, StringComparison.Ordinal))
            return Compiler.PlatformCompatibility.DeclineReason(
                definition.CompiledFrameworkVersion, definition.CompiledPlatformVersion,
                definition.PlatformCeiling, liveFrameworkVersion, livePlatformVersion) is { } range
                ? $"its record names an assembly whose platform range excludes this process: {range}"
                : null;
        return string.IsNullOrEmpty(definition.CompiledFrameworkVersion)
            ? $"its record names an assembly but records NO framework build identity, so those "
              + $"bytes cannot be shown ABI-compatible with the live framework "
              + $"{Short(liveFrameworkVersion)}"
            : $"its record names an assembly built against framework "
              + $"{Short(definition.CompiledFrameworkVersion)} and this process is "
              + $"{Short(liveFrameworkVersion)}";
    }

    /// <summary><see cref="RefusalReason(NodeTypeDefinition?, string)"/> against the LIVE framework
    /// identity of this process.</summary>
    public static string? RefusalReason(NodeTypeDefinition? definition)
        => RefusalReason(definition, NodeTypeCompilationHelpers.FrameworkVersion);

    /// <summary>Convenience for the call sites that only need the yes/no.</summary>
    public static bool Refuses(NodeTypeDefinition? definition)
        => RefusalReason(definition) is not null;

    /// <summary>
    /// The refusal as ONE English sentence, for the LOG line — the machine/operator-facing surface,
    /// which stays English by house rule (log lines ship to Loki).
    ///
    /// <para>🚨 Both identities, always. The pair is what makes a refusal checkable by hand against
    /// the CD run that produced the bytes, and naming only one is how the 2026-09-06 bake gate's
    /// "regressed on this image" sent three investigations to the wrong repository.</para>
    /// </summary>
    public static string RefusalSummary(string nodeTypePath, NodeTypeDefinition definition, string liveFrameworkVersion)
        => $"Adoption refused for NodeType '{nodeTypePath}': "
           + (RefusalReason(definition, liveFrameworkVersion) ?? "no refusal")
           + $" (record names {definition.LatestAssemblyCollection}/{definition.LatestAssemblyPath}). "
           + "An assembly compiled for one framework identity is not loadable by a process running "
           + "another — the failure would surface as a TypeLoadException inside a collectible ALC "
           + "at activation, with no compile error and nothing to grep (#3472).";

    /// <summary><see cref="RefusalSummary(string, NodeTypeDefinition, string)"/> against the LIVE
    /// framework identity.</summary>
    public static string RefusalSummary(string nodeTypePath, NodeTypeDefinition definition)
        => RefusalSummary(nodeTypePath, definition, NodeTypeCompilationHelpers.FrameworkVersion);

    /// <summary>
    /// The recovery verb, stated wherever the refusal is. A refusal that does not say what happens
    /// next reads as "the type is dead", and here it is emphatically not.
    ///
    /// <para>🚨 It states BOTH populations' recoveries in one sentence, so it must not decide a log
    /// LEVEL: a log line goes through <see cref="NodeTypeAdoptionRefusalLog"/>, which states the
    /// ONE recovery that applies on this mesh (<see cref="RecoveryVerbFor"/>) at the level that
    /// matches it (#5066). This text remains for surfaces that carry no level (the pinned-release
    /// refusal reason).</para>
    /// </summary>
    public const string RecoveryVerb =
        "Nothing further is required: this is the state the compile watcher already heals. The "
        + "type's own hub rebuilds it against the live framework and restamps the record, exactly "
        + "as it does after any ordinary platform roll. On a `Modules:RequirePrebuilt` mesh no "
        + "local compile is possible — rebake and republish the package for THIS framework "
        + "identity.";

    /// <summary>
    /// The recovery for a refusal on a mesh that CAN compile locally — the expected state after a
    /// platform roll, healed by the type's own hub (#5066).
    /// </summary>
    public const string RecoveryVerbHealedHere =
        "Nothing further is required: this is the state the compile watcher already heals. The "
        + "type's own hub rebuilds it against the live framework and restamps the record, exactly "
        + "as it does after any ordinary platform roll. If it persists, read bake-report's LIVE "
        + "RECORD CENSUS and the instance's converged/generations — a roll that never converges "
        + "re-stamps the record from the other generation.";

    /// <summary>
    /// The recovery for a refusal on a <c>Modules:RequirePrebuilt</c> mesh, where nothing on this
    /// process heals it (#5066).
    /// </summary>
    public const string RecoveryVerbRequirePrebuilt =
        "This mesh sets `Modules:RequirePrebuilt`, so no local compile is possible and NOTHING on "
        + "this process heals it: rebake and republish the package for THIS framework identity.";

    /// <summary>The recovery sentence for a refusal on a mesh that can (or cannot) compile
    /// locally.</summary>
    public static string RecoveryVerbFor(bool canCompileLocally)
        => canCompileLocally ? RecoveryVerbHealedHere : RecoveryVerbRequirePrebuilt;

    /// <summary>
    /// 🚨 <b>The level a refusal is logged at — decided by whether it heals here</b> (#5066).
    ///
    /// <para>Where a local compile is possible the refusal is the expected transition of every
    /// platform roll and the compile watcher heals it: <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/>.
    /// Logged at <c>Error</c>, one unconverged roll turned it into 507 incident-grade lines an hour
    /// on one pod. On a <c>Modules:RequirePrebuilt</c> mesh nothing on this process heals it — only
    /// a rebake does — so it is actionable: <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>.
    /// This is a cost/value classification, not a verbosity dial.</para>
    /// </summary>
    public static Microsoft.Extensions.Logging.LogLevel RefusalLogLevel(bool canCompileLocally)
        => canCompileLocally
            ? Microsoft.Extensions.Logging.LogLevel.Warning
            : Microsoft.Extensions.Logging.LogLevel.Error;

    /// <summary>
    /// 🚨 <b>The status this PROCESS may honestly report for this record</b> — criterion 2 of
    /// #3472, and the one function every reporting surface must go through.
    ///
    /// <para>Everything except a successful build with a recorded assembly passes through
    /// untouched: <see cref="CompilationStatus.Error"/> is still "correct the code",
    /// <see cref="CompilationStatus.Pending"/> / <see cref="CompilationStatus.Compiling"/> are
    /// still in flight, <see cref="CompilationStatus.Unavailable"/> is still "could not be
    /// determined". A successful build whose identity is this process's is still
    /// <see cref="CompilationStatus.Ok"/>. A successful build whose identity is NOT is
    /// <see cref="CompilationStatus.Foreign"/> — never <c>Ok</c>, and never <c>Error</c>.</para>
    ///
    /// <para>An <c>Ok</c> that records no assembly at all stays <c>Ok</c>: a marker type, or a type
    /// whose compile produced no bytes, claims nothing about a build and so cannot be claiming a
    /// foreign one.</para>
    /// </summary>
    public static CompilationStatus? ReportedStatus(
        NodeTypeDefinition? definition, string liveFrameworkVersion)
        => ReportedStatus(definition, liveFrameworkVersion, NodeTypeCompilationHelpers.LivePlatformVersion);

    /// <summary><see cref="ReportedStatus(NodeTypeDefinition?, string)"/> with the running platform
    /// BUILD explicit — a record whose platform range excludes it reports
    /// <see cref="CompilationStatus.Foreign"/> (policy <c>platform-backwards-compatibility</c>).</summary>
    public static CompilationStatus? ReportedStatus(
        NodeTypeDefinition? definition, string liveFrameworkVersion, string? livePlatformVersion)
        => definition?.CompilationStatus is not CompilationStatus.Ok
            ? definition?.CompilationStatus
            : RefusalReason(definition, liveFrameworkVersion, livePlatformVersion) is null
                ? CompilationStatus.Ok
                : CompilationStatus.Foreign;

    /// <summary><see cref="ReportedStatus(NodeTypeDefinition?, string)"/> against the LIVE framework
    /// identity of this process.</summary>
    public static CompilationStatus? ReportedStatus(NodeTypeDefinition? definition)
        => ReportedStatus(definition, NodeTypeCompilationHelpers.FrameworkVersion);

    /// <summary>
    /// Whether the record's last SUCCESSFUL compile was stamped after <paramref name="bootedAt"/>
    /// — the pure time half of the mid-roll cross-stamp reading. A stamp with no time answers
    /// <see langword="false"/>: an absent reading may not decide a never-benign count in either
    /// direction (the same rule <c>NodeTypeLiveRecordCensus</c> has always applied).
    /// </summary>
    public static bool StampedAfter(NodeTypeDefinition? definition, DateTimeOffset bootedAt)
        => definition?.LastCompileSucceededAt is { } stamped && stamped > bootedAt;

    /// <summary>
    /// 🚨 <b>A replica on ANOTHER image owns this type now</b> — the record names a build for a
    /// framework this process does not run (<see cref="CompilationStatus.Foreign"/>) AND that
    /// build was stamped after this process started. Before boot, a foreign stamp is the ordinary
    /// state after a platform roll and the compile watcher heals it; after boot it is a
    /// cross-stamp from a newer generation mid-roll, and "healing" it from here re-keys the record
    /// backwards, which the newer replica then heals back — the ping-pong that re-keyed 34 records
    /// on memex's control instance within minutes of its new replica booting. The live record
    /// census reports exactly this set (<c>ForeignSinceBoot</c>); the bind path YIELDS on it rather
    /// than recompile, and both read it through this one function so they cannot disagree.
    /// 🚨 The reading cannot tell which generation is NEWER (a framework identity has no order): the
    /// survivor of a roll reads the same verdict for a record a draining replica re-keyed backwards.
    /// So the bind path yields only while this process is also LEAVING
    /// (<c>NodeTypeEnrichmentHelpers.DecideFrameworkStale</c>); the survivor heals.
    /// </summary>
    public static bool OwnedByANewerGeneration(
        NodeTypeDefinition? definition, string liveFrameworkVersion, DateTimeOffset bootedAt)
        => ReportedStatus(definition, liveFrameworkVersion) is CompilationStatus.Foreign
            && StampedAfter(definition, bootedAt);

    /// <summary>
    /// 🚨 <b>A NEWER platform build provably owns this record</b> — the record carries THIS
    /// process's compatibility key but a platform FLOOR newer than the running build (policy
    /// <c>platform-backwards-compatibility</c>). Unlike <see cref="OwnedByANewerGeneration"/>, the
    /// ORDER is known here (the platform build carries a run ordinal), so the bind path yields on it
    /// UNCONDITIONALLY — leaving or not, before boot or after: recompiling it from here would
    /// re-key a newer replica's record backwards, which is the re-key war a mixed roll must never
    /// fight. The newer replica keeps it; this process serves nothing it cannot load, and says so.
    /// Pure.
    /// </summary>
    /// <param name="definition">The NodeType record.</param>
    /// <param name="liveFrameworkVersion">This process's compatibility key.</param>
    /// <param name="livePlatformVersion">This process's platform build, or null when unknown.</param>
    public static bool OwnedByANewerPlatformBuild(
        NodeTypeDefinition? definition, string liveFrameworkVersion, string? livePlatformVersion)
        => definition is not null
           && !string.IsNullOrEmpty(definition.LatestAssemblyPath)
           && string.Equals(definition.CompiledFrameworkVersion, liveFrameworkVersion, StringComparison.Ordinal)
           && Compiler.PlatformCompatibility.ProducerIsNewer(definition.CompiledPlatformVersion, livePlatformVersion);

    /// <summary>First eight characters — the same width the assembly-store filename tag carries, so
    /// a log line and a DLL name can be compared by eye.</summary>
    /// <summary>
    /// Why a PINNED release (<see cref="NodeTypeDefinition.RequestedReleasePath"/>) may NOT be adopted
    /// by this process, or <see langword="null"/> when it may — the pinned-release half of the
    /// admission rule, decided from the release's <see cref="NodeTypeRelease.Artifacts"/>.
    ///
    /// <para>🚨 <b>Never from <see cref="NodeTypeRelease.FrameworkVersion"/>.</b> That field is the
    /// framework's ASSEMBLY VERSION string (<c>3.0.0.0</c>) and its own doc says it "has never gated
    /// adoption"; the value that does is the build IDENTITY on each artifact link. #3472 compared the
    /// two, so from 3.0.0-ci.7939 every pin to a historical release was refused on every mesh —
    /// "built against framework 3.0.0.0 and this process is 1deb…" — the exact producer/gate
    /// disagreement #1696 recorded, one door over (MeshWeaver.Plugins'
    /// <c>CodeEditRecompileTest.NodeType_RequestedReleasePath_PinsToHistoricalRelease</c> caught it;
    /// this repository had no test that pins a release).</para>
    ///
    /// <para>Three answers. An artifact for THIS identity and a runnable architecture → admitted.
    /// Artifacts present, none for this identity → refused, naming what the release offers (that is
    /// the case #3472 exists for: bytes keyed to another framework must not load here). NO artifact
    /// link at all → admitted, UNVERIFIED: a release written before artifact links existed states no
    /// identity, and refusing on an absence is the inconclusive-probe mistake (#890) — the same
    /// choice <c>ApplyAdoptedSourceStamp</c> makes for a legacy bundle. Pure.</para>
    /// </summary>
    public static string? PinnedReleaseRefusal(
        NodeTypeRelease release, string liveFrameworkIdentity, string liveArchitecture)
    {
        if (release.Artifacts is not { Count: > 0 })
            return null;
        var match = ReleaseArtifactResolver.Resolve([release], liveFrameworkIdentity, liveArchitecture);
        if (match.IsResolved)
            return null;
        return $"pinned release '{release.Path}' carries no artifact for framework "
            + $"{Short(liveFrameworkIdentity)} on {liveArchitecture} — {match.DeclineReason}. {RecoveryVerb}";
    }

    /// <summary>Whether a pinned release is admitted WITHOUT an identity to check — it predates
    /// artifact links. The caller logs it; the answer is "adopt, unverified", never a refusal.</summary>
    public static bool IsPinnedReleaseUnverified(NodeTypeRelease release)
        => release.Artifacts is not { Count: > 0 };

    private static string Short(string? identity)
        => string.IsNullOrEmpty(identity)
            ? "(none)"
            : identity[..Math.Min(8, identity.Length)];
}
