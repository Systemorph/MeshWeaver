using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Adopts an assembly that was compiled somewhere else — CI, a bake service, an installed package —
/// as a NodeType's build, so the portal never compiles what has already been compiled.
///
/// <para>See <c>Doc/Architecture/PluginPackaging</c> for how such an assembly is produced. This is
/// the consuming half.</para>
/// </summary>
public static class PrebuiltAssemblySeeder
{
    /// <summary>
    /// Deployment opt-in that turns the compile FALLBACK into a NAMED, early error: a mesh that
    /// sets <c>Modules:RequirePrebuilt</c> to <c>true</c> refuses to Roslyn-compile module content
    /// whose prebuilt assemblies are missing — the miss fails the install/update immediately,
    /// naming the package, the registry, the framework identity/architecture and the fix
    /// (publish or rebake the bundle for this lane) instead of quietly compiling.
    ///
    /// <para>🚨 <b>Default OFF.</b> Compiling stays the correct fallback wherever the bake lanes do
    /// not yet cover the mesh's identity (local/dev meshes, CI's disposable meshes, the bake mesh
    /// itself — those are the places compiling remains legal). A PRODUCTION portal opts in because
    /// its invariant is "the runtime artifact of a module is a baked DLL": a silent compile there
    /// is a distribution failure being papered over — the 2026-08-25 Store incident's
    /// "carried no assemblies — compiling instead" family. Design of record:
    /// Systemorph/MeshWeaver#2193 §A. Sibling policy key:
    /// <c>NodeTypeEnrichmentHelpers.AutoRecycleConfigKey</c> (convergence after a publish).</para>
    /// </summary>
    public const string RequirePrebuiltConfigKey = "Modules:RequirePrebuilt";

    /// <summary>Reads <see cref="RequirePrebuiltConfigKey"/> — absent or unparseable means OFF,
    /// the compile-fallback default. Never throws.</summary>
    public static bool RequirePrebuilt(IServiceProvider? services)
    {
        try
        {
            var value = services?
                .GetService<Microsoft.Extensions.Configuration.IConfiguration>()?[RequirePrebuiltConfigKey];
            return bool.TryParse(value, out var parsed) && parsed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The NAMED refusal a require-prebuilt mesh parks a NodeType with when it would otherwise
    /// compile on a miss (the compile watcher's adopt-only gate): what is missing, for which
    /// lane, what publishes it, how to retry. One shape for the whole family — the same facts the
    /// install-lane <see cref="PrebuiltRequiredException"/> names — so an operator reads the same
    /// sentence whichever seam refused. The package is the type's partition root (the node-repo
    /// layout: <c>{Package}/{Type}</c>). Pure.
    /// </summary>
    public static string RequiredParkReason(string nodeTypePath)
    {
        var slash = nodeTypePath.IndexOf('/');
        var package = slash > 0 ? nodeTypePath[..slash] : nodeTypePath;
        return $"{RequirePrebuiltConfigKey}: NodeType '{nodeTypePath}' has no adopted assembly for "
            + $"framework {LiveFrameworkMvid}/{ReleaseArchitecture.Live}, and this mesh does not "
            + $"compile module content. Publish or rebake package '{package}' for this framework "
            + "identity and architecture, then request a release to retry (MeshWeaver#2193 §A).";
    }

    /// <summary>
    /// Why a prebuilt assembly may NOT be adopted, or null when it may.
    ///
    /// <para>🚨 This is the whole safety argument, kept as one pure function so it can be tested
    /// without standing up a mesh. <c>FrameworkVersion</c> is the resolved framework build
    /// identity (<see cref="FrameworkBuildIdentity.FrameworkVersion"/> — surface hash / commit
    /// stamp / toolchain-anchor MVID) — and the assembly-store key carries its first eight
    /// characters. Seeding bytes built against a different framework therefore writes them under
    /// the LIVE framework's tag, where the store reports them as a usable build: the rebuild that
    /// was needed is suppressed, and the ABI mismatch surfaces as a <c>TypeLoadException</c>
    /// inside a collectible ALC at activation — no overlay, no compile error, nothing to
    /// grep.</para>
    ///
    /// <para>Declining is always safe (the caller compiles, as it does today). Adopting on faith is
    /// not. So anything short of an exact match declines — including an absent identity, which is
    /// what a producer that predates MVID recording emits.</para>
    /// </summary>
    /// <para>Public because the consuming side asks it BEFORE unpacking a downloaded bundle — the
    /// gate below is still the one that holds, but re-seeding a whole bundle only to decline every
    /// assembly individually wastes the download and buries the one reason in N identical lines.</para>
    public static string? DeclineReason(string? frameworkMvid) =>
        DeclineReason(frameworkMvid, NodeTypeCompilationHelpers.FrameworkVersion);

    /// <summary>
    /// <see cref="DeclineReason(string?)"/> against an EXPLICIT live identity — the pure core, so a
    /// test can stage a framework roll without rebuilding the framework (the same seam
    /// <c>NodeTypeBakeStatus.Classify</c> exposes for the same reason).
    /// </summary>
    public static string? DeclineReason(string? frameworkMvid, string liveFrameworkMvid) =>
        string.IsNullOrEmpty(frameworkMvid)
            ? $"the producer recorded no framework identity, so it cannot be shown ABI-compatible "
              + $"with the live framework {liveFrameworkMvid}"
            : !string.Equals(frameworkMvid, liveFrameworkMvid, StringComparison.Ordinal)
                ? $"built against framework {frameworkMvid}, live framework is "
                  + $"{liveFrameworkMvid}"
                : null;

    /// <summary>
    /// The LIVE framework identity a producer must record beside its bytes — the resolved
    /// framework build identity (<see cref="FrameworkBuildIdentity.FrameworkVersion"/>), exactly
    /// as <see cref="DeclineReason(string?)"/> compares it. The one public reading of this
    /// identity, so a producer (the CI bake, the registry bundle lane) and the consuming gate can
    /// never disagree about what "the framework identity" is.
    /// </summary>
    public static string LiveFrameworkMvid => NodeTypeCompilationHelpers.FrameworkVersion;

    /// <summary>
    /// Degradation warning from the live identity's resolution (a torn or unusable surface
    /// manifest fell back to the stamp/MVID layer — see
    /// <see cref="FrameworkBuildIdentity.ResolveProcessIdentityWithDiagnostics"/>), or null on
    /// the happy path. Public beside <see cref="LiveFrameworkMvid"/> so the process that
    /// announces the identity (the pre-warmer) can announce the degradation with it.
    /// </summary>
    public static string? LiveFrameworkIdentityWarning => NodeTypeCompilationHelpers.FrameworkVersionWarning;

    /// <summary>
    /// Whether adopting this bundle entry would CHANGE ANYTHING — i.e. whether the NodeType's own
    /// record already names exactly the build the seed would stamp, with the bytes still behind it.
    ///
    /// <para>🚨 The counterpart to <see cref="DeclineReason(string?)"/>, and the reason both are
    /// public: <c>DeclineReason</c> answers "must we NOT adopt", this answers "need we adopt at
    /// all". Without it every boot re-adopts every bundle entry it holds — and adoption is not
    /// cheap bookkeeping: <see cref="Seed(IMessageHub, string, byte[], byte[], string, ILogger, IReadOnlyDictionary{string, string}, string)"/> opens the type's own mesh-node stream (which ACTIVATES
    /// its per-node hub), re-uploads the assembly bytes to the shared store, and writes the node.
    /// Measured on memex-cloud 2026-08-17, that was 43 hub activations, 43 uploads and 43 writes —
    /// <b>13.5 s of a 101 s boot</b> — establishing that nothing had changed since the previous pod
    /// did exactly the same thing. Framework identity is an API-SURFACE hash, deliberately stable
    /// across internal-only merges, so the overwhelmingly common roll finds its own bytes already
    /// on the share.</para>
    ///
    /// <para><b>Pure, and level-triggered on the store.</b> The caller resolves
    /// <paramref name="storeHasBytes"/> by probing the store at the record's
    /// <see cref="NodeTypeDefinition.LastCompiledVersion"/> and passes the answer in, so this
    /// function does no I/O. That split is the same one <see cref="NodeTypeBakeStatus.Classify"/>
    /// makes, for the same reason: a record can claim a build whose bytes were cleared, remounted
    /// or restored away from under it (<see cref="BakeState.BytesMissing"/>), and a skip decided on
    /// the record alone would leave that type permanently unbuilt. Record AND bytes ⇒ skip;
    /// anything else ⇒ seed.</para>
    ///
    /// <para><b>"Already built" is NOT re-decided here.</b> It delegates to
    /// <see cref="NodeTypeBakeStatus.Classify"/> — the one definition of that in the framework, and
    /// the very function the pre-warmer's store probe applies to this same type moments later. A
    /// second, hand-rolled notion of "current" beside it would be a rule that can drift from the
    /// one that actually decides whether anything gets compiled, and the two disagreeing is how a
    /// type gets skipped by the seeder AND skipped by the bake. So the contract is exactly: <b>skip
    /// precisely what the bake would call <see cref="BakeState.Baked"/></b>.</para>
    ///
    /// <para>🚨 Note in particular what this does NOT compare: the NodeType MeshNode's CURRENT
    /// version. <see cref="Seed(IMessageHub, string, byte[], byte[], string, ILogger, IReadOnlyDictionary{string, string}, string)"/> uploads and stamps the version it read BEFORE its own write, and
    /// that write bumps the node — so after any adoption <c>LastCompiledVersion</c> is permanently
    /// one behind <c>node.Version</c>, and a naive equality check is unsatisfiable. That mismatch
    /// is also why today's unconditional re-adoption uploads the SAME bytes under a NEW store key
    /// every boot: the assembly cache grows a fresh generation per pod start with nothing ever
    /// reading the old ones. Whether a build is stale with respect to its SOURCES is
    /// <c>CompiledSources</c>/<c>CurrentSourceVersions</c>'s question, answered by the compile
    /// watcher, and it is deliberately not re-asked here.</para>
    ///
    /// <para>The one conjunct this adds on top of the bake's own verdict is the bundle's DEPENDENCY
    /// RECORD (#1707 slice 2): an entry whose record differs from the stamped one would REPLACE
    /// that stamp, so it is a real change and must not be mistaken for a no-op. A legacy bundle
    /// carrying no record stamps none, and therefore cannot change one either.</para>
    /// </summary>
    /// <param name="definition">The NodeType's persisted definition.</param>
    /// <param name="storeHasBytes">Whether the assembly store resolved bytes for this type's
    /// <see cref="NodeTypeDefinition.LastCompiledVersion"/> — the caller's already-resolved probe,
    /// so this function never does I/O.</param>
    /// <param name="bundleDependencies">The bundle entry's dependency record, or null for a
    /// legacy bundle.</param>
    /// <param name="liveFrameworkMvid">Framework identity to compare against; defaults to the live
    /// one. Injected so a test can stage a framework roll without rebuilding the framework — the
    /// same seam <see cref="DeclineReason(string?, string)"/> exposes.</param>
    /// <param name="liveDependencyIdOf">Live dependency-id resolver, passed through to
    /// <see cref="NodeTypeBakeStatus.Classify"/>.</param>
    /// <param name="liveToolchainId">Live toolchain id, passed through to
    /// <see cref="NodeTypeBakeStatus.Classify"/>.</param>
    public static bool IsAlreadyAdopted(
        NodeTypeDefinition definition,
        bool storeHasBytes,
        IReadOnlyDictionary<string, string>? bundleDependencies,
        string? liveFrameworkMvid = null,
        Func<string, string?>? liveDependencyIdOf = null,
        string? liveToolchainId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return StampWouldMatch(definition.CompiledDependencies, bundleDependencies)
            && NodeTypeBakeStatus.Classify(
                definition,
                storeHasBytes,
                liveFrameworkMvid ?? LiveFrameworkMvid,
                liveDependencyIdOf,
                liveToolchainId) is BakeState.Baked;
    }

    /// <summary>
    /// Whether re-stamping <paramref name="bundle"/> would leave <paramref name="stamped"/>
    /// unchanged. A legacy bundle (null) stamps nothing, so it always would.
    /// </summary>
    private static bool StampWouldMatch(
        IReadOnlyDictionary<string, string>? stamped,
        IReadOnlyDictionary<string, string>? bundle)
    {
        if (bundle is null)
            return true;
        if (stamped is null || stamped.Count != bundle.Count)
            return false;
        foreach (var (key, value) in bundle)
            if (!stamped.TryGetValue(key, out var current)
                || !string.Equals(current, value, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// 🚨 <b>THE TRAP-DOOR THAT MADE #2813's CURE INERT. Do not call it; pass the fingerprint.</b>
    ///
    /// <para>This overload defaulted <c>sourceFingerprint</c> to null, and BOTH production callers
    /// bound to it — <c>PluginBundleClient</c> and <c>ShippedPrebuiltBundles</c>. So every adoption
    /// on every mesh took the legacy "provenance unknown" branch, the three-way comparison in
    /// <c>NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp</c> could never reach its refusal, and
    /// the whole mechanism sat armed and dead for months while the code implementing it looked
    /// finished. A convenience overload whose default answer is "unverified" is not a convenience:
    /// it is a silent opt-out of a safety check, taken by whoever writes the shortest call.</para>
    ///
    /// <para>Passing <c>null</c> is still legitimate — a legacy bundle genuinely records no
    /// fingerprint — but it must be WRITTEN, at the call site, where a reviewer can see the claim
    /// being waived. Use the eight-parameter
    /// <see cref="Seed(IMessageHub, string, byte[], byte[], string, ILogger, IReadOnlyDictionary{string, string}, string)"/>.</para>
    ///
    /// <para>🚨 <b>Why this is <c>Obsolete(error)</c> and not DELETED.</b> Deleting a public
    /// framework method is a breaking change to in-mesh code no compiler can see, and AGENTS.md
    /// requires sweeping the live mesh before doing it. That sweep could not be completed: content
    /// indexing is inactive on both reachable deployments (<c>search_chunks</c> answers
    /// <c>"searched": false</c>, which is a FAILED sweep, not a clean one). Both repos' node trees
    /// were swept by hand and hold no caller. So the symbol stays — nothing already compiled can
    /// break — while every source call site fails loudly, at the call, with the reason.</para>
    /// </summary>
    [Obsolete(
        "#2813: this overload silently adopts prebuilt bytes as UNVERIFIED. Call the eight-parameter "
        + "Seed and pass the bundle's source fingerprint (BundleReader.Payload.SourceFingerprint), "
        + "or an explicit null if the bundle genuinely records none.",
        error: true)]
    public static IObservable<bool> Seed(
        IMessageHub hub,
        string nodeTypePath,
        byte[] assemblyBytes,
        byte[]? pdbBytes,
        string? frameworkMvid,
        ILogger? logger = null,
        IReadOnlyDictionary<string, string>? dependencies = null)
        => Seed(hub, nodeTypePath, assemblyBytes, pdbBytes, frameworkMvid, logger, dependencies,
            sourceFingerprint: null);

    /// <summary>
    /// Seeds <paramref name="assemblyBytes"/> as the build for <paramref name="nodeTypePath"/>,
    /// carrying the producer's source fingerprint so the OWNER can check the adoption (#2813).
    ///
    /// <para>Cold: the write runs on Subscribe. Emits <c>true</c> when the assembly was adopted and
    /// <c>false</c> when it was declined — a decline is not an error, it is the caller's signal to
    /// compile normally.</para>
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="nodeTypePath">Mesh path of the NodeType this assembly implements.</param>
    /// <param name="assemblyBytes">The compiled assembly.</param>
    /// <param name="pdbBytes">Symbols, when the assembly does not embed them.</param>
    /// <param name="frameworkMvid">The framework build identity the bytes were compiled against,
    /// as recorded by the producer. <c>null</c> declines the seed.</param>
    /// <param name="logger">Diagnostics. Every decline is logged with its reason.</param>
    /// <param name="dependencies">The producer's per-type dependency record, or <c>null</c> for a
    /// legacy bundle.</param>
    /// <param name="sourceFingerprint">🚨 <b>The producer's CONTENT fingerprint of the sources these
    /// bytes were built from</b> — <c>NodeTypeSourceFingerprint.Compute</c> shape (#2813), which is
    /// what <c>BundleReader.Payload.SourceFingerprint</c> carries. Stamped onto the node, where the
    /// OWNER compares it against its own live source set before honouring the adoption's
    /// <c>RequestedSourceStampAt</c>. <c>null</c> for a legacy bundle that records none: the
    /// adoption still lands, but as <c>BuildProvenance.AdoptedUnverified</c> — never silently as
    /// verified.
    ///
    /// <para>A separate OVERLOAD rather than an eighth optional parameter: the seven-parameter form
    /// is public surface, and adding an optional parameter to it is a BINARY break the compatibility
    /// gate is right to refuse. This parameter carries no default, so the two forms stay distinct —
    /// and the seven-argument one is now <c>Obsolete(error)</c>, because a call that silently means
    /// "unverified" is exactly how the refusal above stayed unreachable.</para></param>
    public static IObservable<bool> Seed(
        IMessageHub hub,
        string nodeTypePath,
        byte[] assemblyBytes,
        byte[]? pdbBytes,
        string? frameworkMvid,
        ILogger? logger,
        IReadOnlyDictionary<string, string>? dependencies,
        string? sourceFingerprint)
    {
        // 🚨 THE GATE. FrameworkVersion is the resolved framework build identity — a content/
        // surface identity, not a version string — and the assembly-store key carries the first
        // eight characters of it. So seeding bytes built against a different framework writes
        // them under the LIVE framework's tag, where the store reports them as a usable build:
        // the rebuild that was needed is suppressed, and the ABI mismatch surfaces as a
        // TypeLoadException inside a collectible ALC at activation — no overlay, no compile
        // error, nothing to grep.
        //
        // Declining is always safe; adopting on faith is not. So an identity that is absent or
        // different declines, and the caller compiles.
        if (DeclineReason(frameworkMvid) is { } reason)
        {
            logger?.LogInformation(
                "Prebuilt assembly for {NodeTypePath} DECLINED: {Reason} — compiling instead",
                nodeTypePath, reason);
            return Observable.Return(false);
        }

        // The per-type dependency record (#1707 slice 2): the framework gate above proves the
        // PLATFORM surface matches; this proves the MODULE/toolchain bindings do too — the store
        // key cannot see either, so an unvalidated adopt would suppress a needed rebuild exactly
        // like a framework mismatch would.
        if (dependencies is not null
            && CompiledDependencies.FindMismatch(
                dependencies,
                NodeTypeCompilationHelpers.DependencyIdResolverOf(hub),
                NodeTypeCompilationHelpers.ProcessToolchainId) is { } dependencyMismatch)
        {
            logger?.LogInformation(
                "Prebuilt assembly for {NodeTypePath} DECLINED: dependency record mismatch — "
                + "{Mismatch} — compiling instead",
                nodeTypePath, dependencyMismatch);
            return Observable.Return(false);
        }

        // 🚨 DEFERRED — the leaving check below must run at SUBSCRIBE time, not at call time. The
        // bundle seeder builds one Seed per assembly and runs them under Concat, so each is
        // subscribed only when its predecessor completes; a check taken when the pipeline was
        // BUILT would describe the process as it was minutes earlier. Evaluated here, it is the
        // per-node boundary at which a pass in flight stops once shutdown begins.
        return Observable.Defer(() =>
        {
            // 🚨 #3129 — A LEAVING HUB WRITES NOTHING ON A NODE EVERY GENERATION SHARES. The
            // NodeType node is one record for the whole deployment; a pod under SIGTERM (a 30-minute
            // termination grace period, circuits still held) is not the pod that will serve this
            // type, and its adoption is exactly as authoritative as a healthy pod's — which is the
            // problem: the stamp below REPLACES the coordinates the live build serves under, and
            // when the owner then refuses the adoption (#2813) it CLEARS them. On the roll measured
            // in #3129 the terminating pod did that 1424 times in 25 minutes, and every healthy
            // pod's instances of the type sat on the fallback card for the 120 s self-heal bound,
            // per type, per roll. Same rule as #3109 gave BuildupActions: nothing starts on a hub
            // that is leaving. Emits false — "not adopted", the caller's ordinary compile signal —
            // so a seeding pass is never parked on it. IsLeaving reads the host lifetime too, not
            // only IsShuttingDown: the mesh is disposed at the very END of host shutdown, so the
            // hub signal alone is false for the whole grace period (see HubLeavingExtensions).
            if (hub.IsLeaving())
            {
                logger?.LogInformation(
                    "Prebuilt assembly for {NodeTypePath} NOT seeded: this hub is leaving (#3129) — "
                    + "a shutting-down process writes nothing on a NodeType every generation shares; "
                    + "the next generation seeds its own bundles",
                    nodeTypePath);
                return Observable.Return(false);
            }

            var workspace = hub.GetWorkspace();

            // 🚨 RESERVE BEFORE TOUCHING THE STREAM (#1763). Opening the type's node stream ACTIVATES
            // its hub, and activation is what arms the first-build kickoff — so without the
            // reservation the seeder's own probe started the Roslyn compile this adoption exists to
            // avoid, and that compile overwrote the adopted build milliseconds later. The reservation
            // has to be taken before the subscribe below, which is why it wraps the whole pipeline in
            // an Observable.Using rather than sitting inside a SelectMany. See
            // NodeTypeAdoptionRegistry for the measured trace.
            var reservations = hub.ServiceProvider.GetService<NodeTypeAdoptionRegistry>();

            return Observable.Using(
                () => reservations?.Reserve(nodeTypePath) ?? NoReservation.Instance,
                _ => workspace.GetMeshNodeStream(nodeTypePath)
                .Where(node => node is not null)
                .Take(1)
                .SelectMany(node => SeedObserved(
                    hub, workspace, node!, nodeTypePath, assemblyBytes, pdbBytes, logger,
                    dependencies, sourceFingerprint)));
        });
    }

    /// <summary>The half of <see cref="Seed(IMessageHub, string, byte[], byte[], string, ILogger, IReadOnlyDictionary{string, string}, string)"/>
    /// that runs once the owner's current snapshot of the node is in hand.</summary>
    private static IObservable<bool> SeedObserved(
        IMessageHub hub,
        IWorkspace workspace,
        MeshNode node,
        string nodeTypePath,
        byte[] assemblyBytes,
        byte[]? pdbBytes,
        ILogger? logger,
        IReadOnlyDictionary<string, string>? dependencies,
        string? sourceFingerprint)
    {
        var observed = node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
        if (observed is null)
        {
            logger?.LogInformation(
                "Prebuilt assembly for {NodeTypePath} DECLINED: node content is not a "
                + "NodeTypeDefinition", nodeTypePath);
            return Observable.Return(false);
        }

        // 🚨 #2813 / #3129 — DECLINE BEFORE WRITING WHEN THE REFUSAL IS ALREADY DECIDABLE. The
        // owner's three-way check (ApplyAdoptedSourceStamp) exists because this write is
        // cross-hub and the owner's live source set may not be published yet (#1834) — an
        // INCONCLUSIVE snapshot must not refuse. But the snapshot in hand here is the owner's own
        // current state, and when it ALREADY carries a live fingerprint that disagrees with the
        // producer's, the refusal is certain: sources only ever move the live fingerprint further
        // from a bundle baked earlier. Writing anyway is what #3129 measured as the clobber — the
        // stamp REPLACES the coordinates of the build actually serving with the stale bundle's,
        // the owner refuses, and the refusal has nothing left to leave in place but the rejected
        // build's coordinates, so it clears them and every other pod's instances lose the type
        // until a fresh compile lands. Declining here leaves the live build's record untouched,
        // exactly like the framework and dependency declines above; the caller compiles, as it
        // would have after the refusal. The owner's check stays for the pre-publication window
        // this snapshot cannot decide (no live fingerprint yet) and as the last line of defence.
        // A decline is always safe (a compile follows); a write that a refusal must undo is not.
        if (sourceFingerprint is { Length: > 0 } producerFingerprint
            && observed.CurrentSourceFingerprint is { Length: > 0 } liveFingerprint
            && !string.Equals(producerFingerprint, liveFingerprint, StringComparison.Ordinal))
        {
            logger?.LogWarning(
                "Prebuilt assembly for {NodeTypePath} DECLINED before writing (#2813): the bundle "
                + "records source fingerprint {Producer} but the live sources are {Live} — the owner "
                + "would refuse the adoption, so the live build's coordinates are left in place and "
                + "the live source compiles instead. Rebake this package to adopt again.",
                nodeTypePath, producerFingerprint, liveFingerprint);
            return Observable.Return(false);
        }

        // 🚨 ONE version, used twice. ApplyCompileSuccess documents why: the stamp must name
        // the SAME version the store upload used, or activation resolves a store key with no
        // bytes behind it, TryGetAssemblyPath misses, and the instance silently falls back
        // to the default configuration.
        var version = node.Version;
        var store = hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        // Set by the lambda on the run that produced the write; false when every run declined
        // (the hub began leaving between the upload and the write), so the caller is told the
        // truth — "not adopted" — rather than the ADOPTED line below over a write that no-opped.
        var stamped = false;

        return store
            .PutWithLocation(nodeTypePath, version, assemblyBytes, pdbBytes)
            .SelectMany(location => workspace.GetMeshNodeStream(nodeTypePath)
                .Update(current =>
                {
                    var def = current?.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                    if (current is null || def is null)
                        return current!;

                    // 🚨 #3129 — re-checked at the write itself: the store upload above is real
                    // I/O, and a shutdown that began during it must not land a stamp from a hub
                    // that is leaving. Returning the node unchanged makes Update a NO-OP (nothing
                    // is posted), so the record other generations read is exactly as it was.
                    if (hub.IsLeaving())
                    {
                        logger?.LogInformation(
                            "Prebuilt assembly for {NodeTypePath} NOT stamped: this hub began "
                            + "leaving during the upload (#3129) — the node is left as it was",
                            nodeTypePath);
                        return current;
                    }

                    stamped = true;
                    return current with
                    {
                        Content = def with
                        {
                            CompilationStatus = CompilationStatus.Ok,
                            CompilationError = null,
                            CompilationDiagnostics = null,
                            LastCompileSucceededAt = DateTimeOffset.UtcNow,
                            LastCompiledVersion = version,
                            LatestAssemblyCollection = location.Collection,
                            LatestAssemblyPath = location.ContentPath,
                            // The adopted bytes' own identity (#2471), read from the image
                            // in hand — no file, no load. An adopted build is exactly the
                            // case where a path says least: several pods adopt the same
                            // bundle under the same key, and a replica that later serves a
                            // different build is invisible to a path comparison.
                            LatestAssemblyMvid =
                                ServedBuildIdentity.OfBytes(assemblyBytes)
                                ?? def.LatestAssemblyMvid,
                            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                            // The adopted build retires any standing FAILURE verdict, so
                            // the inputs it was formed from go with it (#1793) — exactly as
                            // ApplyCompileSuccess does. A token left behind would describe a
                            // verdict this node no longer holds.
                            FailedBuildInputs = null,
                            // 🚨 The source snapshot is stamped BY THE OWNER, not here
                            // (#1834). The producer's own ticks are meaningless on this
                            // mesh (the bake writes zeros), so adoption asserts "these
                            // bytes correspond to the LIVE source set" — and only the
                            // owner knows that set. This write is CROSS-HUB: the lambda
                            // diffs against the MIRROR's snapshot, which predates the
                            // first-activation write of CurrentSourceVersions that this
                            // very subscribe triggers (InstallSourcesWatcher). Reading the
                            // field here therefore stamped CompiledSources = null under a
                            // non-empty CurrentSourceVersions — IsDirty — and the release
                            // request PackageInstaller issues one step later recompiled
                            // the type that had just been adopted. Requesting the stamp
                            // instead has no ordering to lose: whichever of the two writes
                            // lands second carries the owner's authoritative pair.
                            RequestedSourceStampAt = DateTimeOffset.UtcNow,
                            // 🚨 #2813 — WHAT the producer says these bytes were built
                            // from. The owner checks it against its own live source set
                            // when it fulfils the request above; it cannot be checked
                            // here, for the same cross-hub reason the request exists.
                            // Null (a legacy bundle) is carried as null, never as a
                            // match: the owner then records AdoptedUnverified rather
                            // than AdoptedVerified.
                            AdoptedSourceFingerprint = sourceFingerprint,
                            // The producer's dependency record (#1707 slice 2) — validated
                            // above; stamped so ongoing validity checks judge the adopted
                            // build like a locally-compiled one. Legacy bundles (null)
                            // leave any prior stamp untouched.
                            CompiledDependencies = dependencies is null
                                ? def.CompiledDependencies
                                : dependencies.ToImmutableSortedDictionary(
                                    kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                        },
                    };
                }))
            .Select(_ =>
            {
                if (!stamped)
                    return false;
                logger?.LogInformation(
                    "Prebuilt assembly ADOPTED for {NodeTypePath} at version {Version} "
                    + "(framework {Framework}) — no compile needed",
                    nodeTypePath, version, NodeTypeCompilationHelpers.FrameworkVersion);
                return true;
            });
    }

    /// <summary>The no-op reservation handle for a host with no adoption registry (an older or
    /// minimal composition): the interlock is absent, never faked.</summary>
    private sealed class NoReservation : IDisposable
    {
        public static readonly NoReservation Instance = new();

        public void Dispose()
        {
            // Nothing reserved, nothing to release.
        }
    }
}
