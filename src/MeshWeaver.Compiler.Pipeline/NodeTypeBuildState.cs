using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The compile-state seam between the graph model and the NodeType compile pipeline: the
/// build/release predicates a reader uses to decide whether a NodeType is settled and carrying a
/// loadable build, and the best-effort Release write-back the pipeline performs after a compile.
///
/// <para>These are the members that used to sit on <c>MeshDataSourceExtensions</c> in
/// MeshWeaver.Graph. They moved here with the pipeline (the graph/compiler split) because each one
/// reads <c>NodeTypeCompilationHelpers</c>: leaving them behind is what made the graph model and
/// the compile pipeline mutually recursive, and a cycle across an assembly boundary cannot be
/// expressed at all. They stay in the <c>MeshWeaver.Graph</c> NAMESPACE and stay extension
/// methods, so every existing call site binds unchanged.</para>
/// </summary>
public static class NodeTypeBuildState
{
    /// <summary>
    /// Best-effort: write a <c>Release</c> MeshNode at
    /// <c>{nodeTypePath}/Release/{version}</c> capturing the compiled assembly
    /// path + the markdown release notes from the NodeType's
    /// <c>NodeTypeDefinition.ReleaseNotes</c> field.
    ///
    /// <para>🚨 OBSERVED + BOUNDED — never advertise a path before it exists. The
    /// returned observable emits the new release path ONLY after the create has
    /// LANDED (the <c>CreateNode</c> response), or <c>null</c> when it couldn't be
    /// dispatched / didn't land within the bound. The old fire-and-forget shape
    /// returned the path immediately and the caller stamped it into
    /// <c>NodeTypeDefinition.LatestReleasePath</c> — a reader following that field
    /// right after the terminal Ok write then hit a hard path-resolution NotFound
    /// (the un-created node faulted the read stream — the NodeTypeReleaseGateTest
    /// 2-core flake). Same rule as RunCompile's activity-create guard: the stamp
    /// follows the create; it is never a path that does not exist.</para>
    ///
    /// <para>Compile correctness must not depend on the create succeeding — the release MeshNode is
    /// observability + history — so a failure does not fault the compile. See
    /// <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.</para>
    ///
    /// <para>🚨 <b>But a failure is never SILENT, and that is issue #5057.</b> This used to emit a
    /// bare <c>null</c> for every one of its four failure channels, so the reason was thrown away at
    /// the only place that knew it. <see cref="ReleaseCreateOutcome"/> carries the reason out
    /// instead, because the caller that reports the consequence — <c>ReleasePostCondition</c>, which
    /// logs an ERROR and writes the compile <c>_Activity</c> — is not the frame that can see the
    /// cause. An <c>Exception</c>'s stack still goes to the log here, where it exists; the one-line
    /// reason is what travels.</para>
    /// </summary>
    /// <summary>
    /// The re-cut's own release path when a create failed only because that path is already taken,
    /// else <c>null</c>. The WHOLE decision behind #3407, extracted so a test drives it rather than
    /// only its ingredient — pinning <see cref="NodeCreationFailure.IsNodeAlreadyExists"/> alone
    /// would leave the catch free to keep returning <c>null</c> and stay green.
    /// </summary>
    /// <param name="ex">The exception the create failed with.</param>
    /// <param name="releasePath">The path this attempt was minting.</param>
    internal static string? AdoptOnOwnCollision(Exception ex, string releasePath)
        => ex.IsNodeAlreadyExists() ? releasePath : null;

    /// <summary>
    /// How long the observed create may take before the attempt is abandoned.
    ///
    /// <para>🚨 Named, so the refusal can SAY what it waited for — and so no test writes the number
    /// again. It is a bound on a cross-hub round trip inside a compile settle; raising it is not the
    /// remedy for it expiring (a create that cannot land in ten seconds is not a slow create), which
    /// is exactly why the expiry now has to be reportable.</para>
    /// </summary>
    internal static readonly TimeSpan CreateBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What ONE Release-create attempt amounts to: the path when the create LANDED, else the reason
    /// it did not — and <see cref="NotAttempted"/> when none was made.
    ///
    /// <para>🚨 <b>Three states, never two.</b> "Landed", "tried and failed BECAUSE x" and "never
    /// tried" are three different facts about a build, and a <c>string?</c> can hold only two of
    /// them — which is how #5057 came to report <i>"the release could not be re-cut"</i> with
    /// nothing after it, eight times over three minutes across seven node types, while the channel
    /// that produced it (a <see cref="CreateBound"/> that expired) wrote no log line at all.</para>
    /// </summary>
    /// <param name="ReleasePath">The release that now exists, or <c>null</c>.</param>
    /// <param name="Failure">
    /// Why no release exists, in one operator-readable line — <c>null</c> both when the create
    /// landed and when no create was attempted, which <see cref="Attempted"/> separates.
    /// </param>
    /// <param name="Attempted">False only for <see cref="NotAttempted"/>.</param>
    internal sealed record ReleaseCreateOutcome(string? ReleasePath, string? Failure, bool Attempted = true)
    {
        /// <summary>No create was made — this compile was not asked to release anything.</summary>
        internal static readonly ReleaseCreateOutcome NotAttempted = new(null, null, Attempted: false);

        /// <summary>The create landed at <paramref name="releasePath"/>.</summary>
        /// <param name="releasePath">The release that now exists.</param>
        internal static ReleaseCreateOutcome Landed(string releasePath) => new(releasePath, null);

        /// <summary>The create was attempted and did not land, for <paramref name="reason"/>.</summary>
        /// <param name="reason">Why, in one line an operator can act on.</param>
        internal static ReleaseCreateOutcome Failed(string reason) => new(null, reason);

        /// <summary>True when a release exists for these bytes.</summary>
        internal bool Succeeded => ReleasePath is not null;

        /// <summary>
        /// The reason as a clause to append to a sentence — <c>": …"</c> when there is one, and a
        /// sentence that SAYS the reason is missing when there is not. Never the empty string: a
        /// report that trails off is the defect, not the terse form of it.
        /// </summary>
        internal string Because => Failure is { Length: > 0 } reason
            ? $": {reason}"
            : Attempted
                ? " — and the attempt reported no reason, which is itself a defect in this pipeline"
                : " — no create was attempted";
    }

    internal static IObservable<ReleaseCreateOutcome> TryCreateReleaseNode(
        IMessageHub hub,
        string nodeTypePath,
        NodeCompilationResult result,
        MeshNode pendingNode,
        string? activityPath,
        ILogger? logger)
    {
        try
        {
            var meshService = hub.ServiceProvider.GetService<IMeshService>();
            if (meshService is null)
                return Observable.Return(ReleaseCreateOutcome.Failed(
                    "no IMeshService is registered on this hub, so no Release node could be created"));

            // Markdown release notes the author wrote on the NodeType's
            // ReleaseNotes field BEFORE clicking Create Release — sourced
            // from the captured pendingNode (the snapshot at the moment
            // Pending was observed). Reading from the live workspace stream
            // here would race the watcher's already-applied
            // Status=Compiling write.
            var notes = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.ReleaseNotes;

            // Auto-stamp version: {yyyyMMddHHmmss}-{8charContentHash}. Sortable
            // chronologically + unique per content. Hash from the cross-silo
            // durable reference (Collection/ContentPath) so the version is
            // stable across silos — different replicas compiling the same
            // version produce the same release version string. Falls back to
            // the process-local AssemblyLocation when the producer hasn't
            // populated the store fields yet (Null store path), and finally
            // to a fresh GUID so the version is never null.
            var hashSrc = (!string.IsNullOrEmpty(result.Collection) && !string.IsNullOrEmpty(result.ContentPath))
                ? $"{result.Collection}/{result.ContentPath}"
                : result.AssemblyLocation ?? Guid.NewGuid().ToString();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(
                sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashSrc)))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..8];
            var version = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{hash}";

            var releaseNamespace = $"{nodeTypePath}/{GraphNodeTypeNames.ReleaseSegment}";
            var releasePath = $"{releaseNamespace}/{version}";

            // Partition the compiler's combined {path → version} snapshot into
            // source vs. test buckets so the release UI can navigate to each
            // file as-of this release. Classification runs the NodeType's Tests
            // queries (path-prefix heuristic — see CodeQueryResolver.Matches);
            // anything not matching a test query is a source.
            ImmutableDictionary<string, long>? sourceVersions = null;
            ImmutableDictionary<string, long>? testVersions = null;
            if (result.CompiledSources is { Count: > 0 } compiledSources)
            {
                var testQueries = CodeQueryResolver.ExpandAll(
                        pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.Tests,
                        CodeQueryResolver.DefaultTests, nodeTypePath)
                    .ToList();
                testVersions = compiledSources
                    .Where(kv => CodeQueryResolver.Matches(kv.Key, testQueries))
                    .ToImmutableDictionary();
                sourceVersions = compiledSources
                    .Where(kv => !testVersions.ContainsKey(kv.Key))
                    .ToImmutableDictionary();
            }

            var release = new NodeTypeRelease
            {
                Path = releasePath,
                NodeTypePath = nodeTypePath,
                Release = hash,
                Version = version,
                Notes = !string.IsNullOrWhiteSpace(notes)
                    ? Markdown.MarkdownContent.Parse(notes!, "", releasePath)
                    : null,
                FrameworkVersion = typeof(NodeTypeRelease).Assembly
                    .GetName().Version?.ToString() ?? "0.0.0",
                CreatedAt = DateTimeOffset.UtcNow,
                AssemblyPath = result.AssemblyLocation,
                // Cross-silo durable assembly reference — denormalised from the
                // IAssemblyStore upload that produced this compile. Other silos
                // hydrate via these fields; AssemblyPath above is a local-process
                // hint and lies as soon as the Release is read from a remote silo.
                AssemblyCollection = result.Collection,
                AssemblyContentPath = result.ContentPath,
                // Integer version key the IAssemblyStore.Put used. Pinned-release
                // activation calls TryGetAssemblyPath(NodeTypePath, AssemblyStoreVersion)
                // and would otherwise have to parse it back from the display-format
                // `Version` string (yyyyMMddHHmmss-hash), which doesn't preserve
                // the underlying integer.
                AssemblyStoreVersion = result.Version,
                // 🚨 THE LINK (#1751). The two neighbours above are WHERE the bytes are; this is
                // what they may be used FOR — the resolved framework build identity they were
                // compiled against and the architecture that produced them. Recorded here, at the
                // one moment both facts are known for certain (this process just compiled them),
                // so a consumer can resolve "are these mine?" from the node instead of inferring it
                // from an index. FrameworkVersion above stays the assembly version string it always
                // was; conflating the two is what #1696 was.
                Artifacts =
                [
                    new ReleaseArtifact(
                        NodeTypeCompilationHelpers.FrameworkVersion,
                        ReleaseArchitecture.Live,
                        result.Version,
                        result.Collection,
                        result.ContentPath)
                ],
                Status = "Succeeded",
                CompilationActivityPath = activityPath,
                SourceVersions = sourceVersions,
                TestVersions = testVersions
            };

            var node = new MeshNode(version, releaseNamespace)
            {
                Name = $"Release {version}",
                NodeType = GraphNodeTypeNames.Release,
                MainNode = nodeTypePath,
                State = MeshNodeState.Active,
                Content = release
            };

            // Credential split: the surrounding compile (RunCompile) runs as System so the
            // pure compilation fills the assembly cache even on read-only partitions. But the
            // RELEASE node is the user-facing artefact — stamp it to the user who requested it
            // (RequestedReleaseBy, who passed the Compile gate at the entry point) so the
            // release is attributable to its author (owner = caller). When no user requested it
            // (the System-driven Doc-release seed, or the first-build kickoff), RequestedReleaseBy
            // is null and the create falls through under the ambient System scope.
            // Observable.Using acquires the scope AT SUBSCRIBE so both the CreateNode call and
            // its subscription run inside it — CreateNode captures the caller's identity for
            // the stored MeshNode.CreatedBy.
            var requestedBy = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseBy;
            var accessService = hub.ServiceProvider.GetService<AccessService>();

            // OBSERVED create: emit the path only once the create response lands.
            // Bounded — a hung owner must never block the compile's terminal write;
            // on timeout/fault emit null so the parent never advertises a phantom
            // Release path (mirrors RunCompile's activity-create guard).
            return Observable.Using(
                    () => !string.IsNullOrEmpty(requestedBy) && accessService is not null
                        ? accessService.SwitchAccessContext(new AccessContext
                        {
                            ObjectId = requestedBy,
                            Name = requestedBy
                        })
                        : System.Reactive.Disposables.Disposable.Empty,
                    _ => meshService.CreateNode(node).Take(1))
                .Select(_ => ReleaseCreateOutcome.Landed(releasePath))
                // 🚨 A BOUND THAT FAULTS, never one that SUBSTITUTES (#5057). This was
                // `Timeout(bound, Observable.Return<string?>(null))` — the expiry replaced the
                // sequence with the same `null` a refusal produced, wrote NO log line of any kind,
                // and was therefore the one failure channel with no trace whatsoever. It is also the
                // channel the incident names: on `Hosting/InstanceRequest` the "Re-cutting…" line
                // was logged at 22:16:14Z and "…AND the release could not be re-cut" at 22:16:24Z —
                // exactly this bound, expiring, reported by nothing but the gap between two lines
                // that happened to be adjacent. Faulting routes it into the catch below, where it
                // becomes a reason like every other failure.
                .Timeout(CreateBound)
                .Catch<ReleaseCreateOutcome, Exception>(ex =>
                {
                    // 🚨 A COLLISION AT OUR OWN ID IS SUCCESS (#3407). ReleasePostCondition re-cuts
                    // when latestReleasePath still names an earlier build; when the retry lands in
                    // the SAME SECOND as the first attempt, both mint the same id and the second
                    // create throws. Swallowing that into null left the pointer un-advanced: the
                    // bytes were published, the Release node existed, and the type went on
                    // advertising a build no release named — every instance kept executing the
                    // previous assembly behind a $Banner whose own text says a recycle will not
                    // clear it. Measured on memex.localhost 2026-09-06 (Edu/CourseInvite build 767);
                    // only a pod restart cleared it, and nothing in the pipeline did.
                    //
                    // Adopting is naming the same bytes, not guessing. The id is
                    // {yyyyMMddHHmmss}-{8 chars of SHA256(Collection/ContentPath)} — the hash comes
                    // from the DURABLE content reference, so an equal id means equal second AND
                    // equal content. A collision can therefore only be this same code's own earlier
                    // attempt for this same compile. (Healthy re-cuts show in the release list as
                    // PAIRS a second or two apart sharing the suffix; the failing one was the single
                    // unpaired id.)
                    if (AdoptOnOwnCollision(ex, releasePath) is { } adopted)
                    {
                        logger?.LogInformation(
                            "CompileWatcher: Release node at {ReleasePath} already exists — adopting "
                            + "it. The re-cut collided with its own first attempt in the same second; "
                            + "the id encodes the content hash, so this names the same bytes.",
                            adopted);
                        return Observable.Return(ReleaseCreateOutcome.Landed(adopted));
                    }

                    // The STACK stays here, where it exists; the one-line REASON travels out, to the
                    // ERROR line and the compile _Activity that report the consequence (#5057).
                    logger?.LogWarning(ex,
                        "CompileWatcher: failed to create Release node at {ReleasePath}",
                        releasePath);
                    return Observable.Return(ReleaseCreateOutcome.Failed(Describe(ex, releasePath)));
                });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "CompileWatcher: TryCreateReleaseNode threw for {NodeTypePath}", nodeTypePath);
            return Observable.Return(ReleaseCreateOutcome.Failed(
                $"the Release node could not be composed at all: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// One create failure as one operator-readable line. A <see cref="TimeoutException"/> is named
    /// for what it IS — the bound expired and the owning hub never answered — because "TimeoutException:
    /// The operation has timed out" tells a reader nothing about which operation or what it waited for.
    /// Pure.
    /// </summary>
    /// <param name="ex">The exception the create failed with.</param>
    /// <param name="releasePath">The path the attempt was minting.</param>
    internal static string Describe(Exception ex, string releasePath) =>
        ex is TimeoutException
            ? $"the create at '{releasePath}' did not land within {CreateBound} — the owning hub did "
              + "not answer, so whether the node exists is UNKNOWN from here, not no"
            : $"the create at '{releasePath}' failed: {ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Holds a NodeType MeshNode stream until <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// reaches a settled terminal state — anything other than
    /// <see cref="CompilationStatus.Compiling"/> or <see cref="CompilationStatus.Pending"/>.
    /// Lets handlers that depend on the post-compile state (compiled assembly path,
    /// sources snapshot, latest release) wait for the in-progress compile to finish
    /// instead of reading the pre-compile snapshot. Gating on Pending matters too: the
    /// per-NodeType hub's auto-watcher (<c>InstallCompileWatcher</c>) flips Pending →
    /// dispatches an activity compile that writes Compiling. An explicit
    /// <c>CreateReleaseRequest</c> arriving in the Pending window must wait for that
    /// activity to settle rather than racing it with a second inline compile (each
    /// <c>WriteToParent</c> from the racing activity is a <c>DataChangeRequest</c> on
    /// the mesh hub that leaks if the test times out before its response lands).
    /// Non-NodeType nodes pass through unchanged so this is safe to chain on any
    /// MeshNode stream.
    ///
    /// <para>🚨 <b>Read the definition the way its CONSUMER reads it</b> — hence the
    /// <paramref name="options"/>. The predicate used to pattern-match
    /// <c>node.Content is not NodeTypeDefinition</c>, a CLR type test whose "not a NodeType at
    /// all" escape answers SETTLED. That escape also fires for a NodeType node whose Content
    /// arrived UN-MATERIALIZED (a <see cref="JsonElement"/> / <c>JsonNode</c> mirror snapshot —
    /// the normal shape for a node that just crossed a sync stream or was just created), so a
    /// Pending/Compiling type in that shape was admitted as settled and the caller acted on a
    /// pre-compile snapshot. <c>ContentAs</c> recovers every shape, which is exactly what the
    /// downstream consumer uses, so the gate and the consumer can no longer disagree. Same defect,
    /// same fix as <c>NodeTypeEnrichmentHelpers.IsCompileSettled</c>
    /// (<c>CompileSettlePredicateTest</c>); pinned here by <c>LoadableBuildPredicateTest</c>.</para>
    /// </summary>
    /// <param name="source">The NodeType's MeshNode stream.</param>
    /// <param name="options">The reading hub's <c>JsonSerializerOptions</c> — what resolves a
    /// mirror snapshot's <c>$type</c> back into a <see cref="NodeTypeDefinition"/>.</param>
    public static IObservable<MeshNode> AwaitCompilationSettled(
        this IObservable<MeshNode> source, JsonSerializerOptions options)
        => source.Where(node => IsCompilationSettled(node, options));

    /// <summary>
    /// The predicate behind <see cref="AwaitCompilationSettled"/>, as a pure function of one
    /// emission — unit-testable with no hub and no stream.
    /// </summary>
    public static bool IsCompilationSettled(this MeshNode? node, JsonSerializerOptions options)
    {
        var def = node.ContentAs<NodeTypeDefinition>(options);
        return def is null
            || (def.CompilationStatus != CompilationStatus.Compiling
                && def.CompilationStatus != CompilationStatus.Pending);
    }

    /// <summary>
    /// Holds a NodeType MeshNode stream until the type is settled AND is not advertising a build
    /// the framework cannot load — i.e. until an INSTANCE activating against it can be given the
    /// type's real configuration.
    ///
    /// <para>Stricter than <see cref="AwaitCompilationSettled"/> in exactly one way: a settled
    /// <c>Ok</c> whose assembly coordinates are present but whose
    /// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> does not match the live framework
    /// (or whose bytes this process cannot resolve) is NOT accepted. That state is what a node repo
    /// COMMITS — MeshWeaver.Plugins ships <c>Store/Catalog</c> with <c>compilationStatus: Ok</c> and
    /// a July framework hash — and it is transient by construction: the per-NodeType hub's
    /// framework-stale kickoff flips it to Pending and rebuilds. An instance enriched inside that
    /// window binds ONCE to the fallback configuration and then serves only the generic areas
    /// ("No renderer is registered for area <c>Tests</c> on hub <c>Store</c>").</para>
    ///
    /// <para>A type that never compiled at all (no assembly coordinates) and a type whose compile
    /// genuinely FAILED both pass straight through — the assembly fields are only ever written by a
    /// successful compile, so "nothing built" is a settled answer, not a stale build. Callers must
    /// still bound the wait (a type that can never produce a loadable build would otherwise hold
    /// forever) and degrade rather than fail.</para>
    ///
    /// <para>Non-NodeType nodes answer <c>true</c>, so this is safe to ask about any MeshNode —
    /// and that pass-through is decided by <c>ContentAs</c>, never by a CLR type test. A NodeType
    /// node whose Content arrived un-materialized IS a NodeType node; reading it with
    /// <c>Content is not NodeTypeDefinition</c> answered "loadable" for a type that was still
    /// COMPILING, which is the one answer this predicate exists to withhold — the installer then
    /// recycles the retyped root before its in-package type has a build, and the hub that comes
    /// back binds the fallback configuration for its whole lifetime. See
    /// <see cref="AwaitCompilationSettled"/> for the full note.</para>
    ///
    /// <para>🚨 Deliberately a NULL caller of the modules-hash join (#1664 step 11): this is a
    /// pure <see cref="MeshNode"/> predicate with no hub in scope, so it cannot resolve the mesh's
    /// live <c>InstalledModulesFingerprint</c> and passes <c>null</c> — the framework rule alone
    /// governs it. Acceptable because its callers (PackageInstaller's post-install waits) run on
    /// the very mesh that just compiled the build, where the stamped hash IS the live hash; the
    /// hash-decisive gates are the kickoff/enrichment paths, which all pass the live hash.</para>
    /// </summary>
    /// <param name="node">The NodeType MeshNode to judge.</param>
    /// <param name="options">The reading hub's <c>JsonSerializerOptions</c> — what resolves a
    /// mirror snapshot's <c>$type</c> back into a <see cref="NodeTypeDefinition"/>.</param>
    /// <returns>False only while the node is mid-compile or is advertising an unloadable build.</returns>
    public static bool HasLoadableBuild(this MeshNode? node, JsonSerializerOptions options)
    {
        var def = node.ContentAs<NodeTypeDefinition>(options);
        return def is null
            || (def.CompilationStatus != CompilationStatus.Compiling
                && def.CompilationStatus != CompilationStatus.Pending
                && (string.IsNullOrEmpty(def.LatestAssemblyPath)
                    || NodeTypeCompilationHelpers.HasUsableBuild(node!, def)));
    }

    /// <summary>
    /// Stream form of <see cref="HasLoadableBuild"/> — holds a NodeType MeshNode stream until the
    /// type is settled and not advertising a build the framework cannot load. Callers must bound
    /// the wait: a type that can never produce a loadable build would otherwise hold forever.
    /// </summary>
    /// <param name="source">The NodeType's MeshNode stream.</param>
    /// <param name="options">The reading hub's <c>JsonSerializerOptions</c>.</param>
    /// <returns>The same stream, filtered to loadable-build emissions.</returns>
    public static IObservable<MeshNode> AwaitLoadableBuild(
        this IObservable<MeshNode> source, JsonSerializerOptions options)
        => source.Where(node => node.HasLoadableBuild(options));

    internal static bool IsSourcesUpToDate(NodeTypeDefinition? def, IReadOnlyList<MeshNode> currentSources)
    {
        if (def is null || def.CompiledSources is null || string.IsNullOrEmpty(def.LatestReleasePath))
            return false;
        // 🚨 Framework-version gate (issue #464, Defect 1): a cached assembly built against a
        // PREVIOUS framework is not "up to date" even if every source is unchanged — its bytes are
        // ABI-stale after a platform self-update. Report it as needing a rebuild so the UI's
        // Create-Release affordance signals "actionable" rather than "nothing changed".
        if (!string.Equals(def.CompiledFrameworkVersion,
                NodeTypeCompilationHelpers.FrameworkVersion, StringComparison.Ordinal))
            return false;
        var compiled = def.CompiledSources;
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in currentSources)
        {
            if (string.IsNullOrEmpty(source.Path)) continue;
            currentPaths.Add(source.Path);
            // LastModified.UtcTicks (not Version) — must match the snapshot field
            // captured by DiscoverSourceVersionSnapshot. Version is bumped only by
            // the local hub's MeshNodeTypeSource and may not surface through the
            // mesh-level synced query that this handler reads.
            if (!compiled.TryGetValue(source.Path, out var v) || v != source.LastModified.UtcTicks)
                return false;
        }
        foreach (var p in compiled.Keys)
            if (!currentPaths.Contains(p)) return false;
        return true;
    }}
