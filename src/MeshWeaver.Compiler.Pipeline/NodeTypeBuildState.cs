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
    /// 🚨 THE IDENTITY OF THE RELEASE ONE SETTLE OWNS (issue #3407) — minted ONCE, before the first
    /// create attempt, and reused verbatim by every later attempt at that same release.
    ///
    /// <para><b>Why it is a value the caller holds rather than something each attempt mints.</b>
    /// <see cref="TryCreateReleaseNode"/> used to compute <c>{yyyyMMddHHmmss}-{hash}</c> from
    /// <c>DateTime.UtcNow</c> inside its own body. That made the release id a function of WHEN an
    /// attempt ran, so <see cref="ReleasePostCondition"/>'s re-cut — a RETRY of the very same
    /// operation, for the very same bytes — addressed a different node than the attempt it was
    /// retrying. Wall-clock luck then decided which of two wrong outcomes you got: a retry in a
    /// LATER second created a duplicate release for one build (measured on memex 2026-09-06,
    /// <c>Hosting/InstanceAction/Release/20260906113905-cgZ9cItJ</c> and
    /// <c>…113915-cgZ9cItJ</c>, byte-identical content 10.005 s apart), and a retry in the SAME
    /// second minted the id its own first attempt had already created, so the create was refused
    /// "Node already exists", swallowed to <c>null</c>, and <c>LatestReleasePath</c> was never
    /// advanced — the type left advertising a build no release names.</para>
    ///
    /// <para>With the identity held by the settle, a retry is a retry: it targets the node the
    /// first attempt targeted, and the write (an idempotent
    /// <see cref="IMeshService.CreateOrUpdateNode"/>) converges on it whether or not the first
    /// attempt landed. There is nothing left for a second to collide with.</para>
    /// </summary>
    /// <param name="Version">The release id — <c>{yyyyMMddHHmmss}-{8charContentHash}</c>, the
    /// node's <c>Id</c> and the last segment of its path.</param>
    /// <param name="Hash">The 8-char content hash alone, stored as
    /// <see cref="NodeTypeRelease.Release"/>.</param>
    /// <param name="CreatedAt">The instant stamped into <see cref="NodeTypeRelease.CreatedAt"/>.
    /// Minted with the id so a re-attempt writes byte-identical content and the owner's no-op
    /// upsert guard recognises it as one, rather than bumping the node's version for nothing.</param>
    internal readonly record struct ReleaseIdentity(string Version, string Hash, DateTimeOffset CreatedAt);

    /// <summary>
    /// Mints the <see cref="ReleaseIdentity"/> for ONE settle. Call this exactly once per compile
    /// settle (or per bake entry) and hand the result to every attempt at that release.
    ///
    /// <para>The hash is taken from the cross-silo durable reference (Collection/ContentPath) so the
    /// id is stable across silos — different replicas compiling the same version produce the same
    /// hash. It falls back to the process-local AssemblyLocation when the producer has not populated
    /// the store fields yet (Null store path), and finally to a fresh GUID so the id is never
    /// null.</para>
    /// </summary>
    internal static ReleaseIdentity MintReleaseIdentity(NodeCompilationResult result)
    {
        var hashSrc = (!string.IsNullOrEmpty(result.Collection) && !string.IsNullOrEmpty(result.ContentPath))
            ? $"{result.Collection}/{result.ContentPath}"
            : result.AssemblyLocation ?? Guid.NewGuid().ToString();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToBase64String(
            sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashSrc)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..8];
        var now = DateTimeOffset.UtcNow;
        // {yyyyMMddHHmmss}-{8charContentHash}: sortable chronologically, unique per content.
        return new ReleaseIdentity($"{now.UtcDateTime:yyyyMMddHHmmss}-{hash}", hash, now);
    }

    /// <summary>
    /// Best-effort: write a <c>Release</c> MeshNode at
    /// <c>{nodeTypePath}/Release/{identity.Version}</c> capturing the compiled assembly
    /// path + the markdown release notes from the NodeType's
    /// <c>NodeTypeDefinition.ReleaseNotes</c> field.
    ///
    /// <para>🚨 IDEMPOTENT PER <see cref="ReleaseIdentity"/> (issue #3407). The identity is the
    /// caller's — see <see cref="MintReleaseIdentity"/> — and the write is
    /// <see cref="IMeshService.CreateOrUpdateNode"/>, the verb whose own contract is "idempotent
    /// writes that may run concurrently or re-run", decided create-vs-update by the OWNER. So
    /// calling this twice for one settle converges on ONE node and emits ONE path both times; it
    /// can neither duplicate the release nor collide with itself. A create-only write plus a
    /// per-attempt id is what #3407 was.</para>
    ///
    /// <para>🚨 OBSERVED + BOUNDED — never advertise a path before it exists. The
    /// returned observable emits the release path ONLY after the write has
    /// LANDED (the upsert response), or <c>null</c> when it couldn't be
    /// dispatched / didn't land within the bound. The old fire-and-forget shape
    /// returned the path immediately and the caller stamped it into
    /// <c>NodeTypeDefinition.LatestReleasePath</c> — a reader following that field
    /// right after the terminal Ok write then hit a hard path-resolution NotFound
    /// (the un-created node faulted the read stream — the NodeTypeReleaseGateTest
    /// 2-core flake). Same rule as RunCompile's activity-create guard: the stamp
    /// follows the write; it is never a path that does not exist.</para>
    ///
    /// <para>Failures are swallowed (emit <c>null</c>): the release MeshNode is
    /// observability + history. Compile correctness must not depend on the write
    /// succeeding. See <c>Doc/Architecture/ReleaseRecutIdentity</c> and
    /// <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.</para>
    /// </summary>
    internal static IObservable<string?> TryCreateReleaseNode(
        IMessageHub hub,
        string nodeTypePath,
        NodeCompilationResult result,
        MeshNode pendingNode,
        string? activityPath,
        ReleaseIdentity identity,
        ILogger? logger)
    {
        try
        {
            var meshService = hub.ServiceProvider.GetService<IMeshService>();
            if (meshService is null) return Observable.Return<string?>(null);

            // Markdown release notes the author wrote on the NodeType's
            // ReleaseNotes field BEFORE clicking Create Release — sourced
            // from the captured pendingNode (the snapshot at the moment
            // Pending was observed). Reading from the live workspace stream
            // here would race the watcher's already-applied
            // Status=Compiling write.
            var notes = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.ReleaseNotes;

            var (version, hash, createdAt) = identity;

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
                // From the settle's identity, NOT UtcNow: a re-attempt must write byte-identical
                // content so the owner's no-op upsert guard recognises it and adopts without a
                // version bump (#3407).
                CreatedAt = createdAt,
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
            // is null and the write falls through under the ambient System scope.
            // Observable.Using acquires the scope AT SUBSCRIBE so both the CreateOrUpdateNode call
            // and its subscription run inside it — the verb captures the caller's identity as
            // RequestedBy, and its create branch pins that onto the inner CreateNodeRequest's
            // CreatedBy, so attribution is what it always was.
            var requestedBy = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseBy;
            var accessService = hub.ServiceProvider.GetService<AccessService>();

            // 🚨 CreateOrUpdateNode, NOT CreateNode (#3407). This write is re-attempted by
            // ReleasePostCondition whenever the first attempt's OUTCOME was not observed, and with
            // the identity now owned by the settle the re-attempt addresses THIS node. A create-only
            // verb turns that into "Node already exists" — swallowed to null below, leaving
            // LatestReleasePath on the previous build. The upsert is the framework's own answer to
            // exactly this (its doc: "for idempotent writes that may run concurrently or re-run …
            // NOT a client-side CreateNode().Catch(already-exists → UpdateNode())"), the owner
            // serialises create-vs-update so it is race-free, and its no-op guard means an adopt of
            // identical content does not even reach the store.
            //
            // OBSERVED: emit the path only once the write's response lands. Bounded — a hung owner
            // must never block the compile's terminal write; on timeout/fault emit null so the
            // parent never advertises a phantom Release path (mirrors RunCompile's activity-create
            // guard). 🚨 The bound abandons the OBSERVATION, never the write, which is why the
            // retry above must be idempotent: the write it gave up on may well have landed.
            return Observable.Using(
                    () => !string.IsNullOrEmpty(requestedBy) && accessService is not null
                        ? accessService.SwitchAccessContext(new AccessContext
                        {
                            ObjectId = requestedBy,
                            Name = requestedBy
                        })
                        : System.Reactive.Disposables.Disposable.Empty,
                    _ => meshService.CreateOrUpdateNode(node).Take(1))
                .Select(_ => (string?)releasePath)
                .Timeout(TimeSpan.FromSeconds(10), Observable.Return<string?>(null))
                .Catch<string?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "CompileWatcher: failed to write Release node at {ReleasePath}",
                        releasePath);
                    return Observable.Return<string?>(null);
                });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "CompileWatcher: TryCreateReleaseNode threw for {NodeTypePath}", nodeTypePath);
            return Observable.Return<string?>(null);
        }
    }

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
