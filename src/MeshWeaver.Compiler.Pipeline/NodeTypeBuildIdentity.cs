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
    {
        if (definition is null)
            return null;
        if (string.IsNullOrEmpty(definition.LatestAssemblyCollection)
            || string.IsNullOrEmpty(definition.LatestAssemblyPath))
            return null;
        if (string.Equals(
                definition.CompiledFrameworkVersion, liveFrameworkVersion, StringComparison.Ordinal))
            return null;
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
    /// </summary>
    public const string RecoveryVerb =
        "Nothing further is required: this is the state the compile watcher already heals. The "
        + "type's own hub rebuilds it against the live framework and restamps the record, exactly "
        + "as it does after any ordinary platform roll. On a `Modules:RequirePrebuilt` mesh no "
        + "local compile is possible — rebake and republish the package for THIS framework "
        + "identity.";

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
        => definition?.CompilationStatus is not CompilationStatus.Ok
            ? definition?.CompilationStatus
            : RefusalReason(definition, liveFrameworkVersion) is null
                ? CompilationStatus.Ok
                : CompilationStatus.Foreign;

    /// <summary><see cref="ReportedStatus(NodeTypeDefinition?, string)"/> against the LIVE framework
    /// identity of this process.</summary>
    public static CompilationStatus? ReportedStatus(NodeTypeDefinition? definition)
        => ReportedStatus(definition, NodeTypeCompilationHelpers.FrameworkVersion);

    /// <summary>First eight characters — the same width the assembly-store filename tag carries, so
    /// a log line and a DLL name can be compared by eye.</summary>
    private static string Short(string? identity)
        => string.IsNullOrEmpty(identity)
            ? "(none)"
            : identity[..Math.Min(8, identity.Length)];
}
