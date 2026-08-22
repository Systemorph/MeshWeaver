using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// What the seed did — or refused to do — for ONE provider. Every value is a distinct operator
/// action, which is why this is an enum and not a bool: "nothing happened" has four different
/// causes and only one of them is a problem.
/// </summary>
public enum ProviderSeedOutcome
{
    /// <summary>This deployment configures no key for the provider's section — nothing to seed.</summary>
    NoConfiguredKey,

    /// <summary>
    /// The provider NODE does not exist (yet). The static-repo import owns creation; the seed only
    /// ever fills a field on a node that is already there, so this is reported and skipped.
    /// </summary>
    NodeAbsent,

    /// <summary>
    /// The node already carries a key. <b>Administered data always wins</b> — the seed never
    /// overwrites it, whoever wrote it and however it differs from configuration.
    /// </summary>
    AlreadyAdministered,

    /// <summary>The node was keyless and now carries the configured key, encrypted at rest.</summary>
    Seeded,

    /// <summary>
    /// 🚨 REFUSED: no <c>Ai:KeyProtection:MasterKey</c> is configured, so
    /// <see cref="IProviderKeyProtector.Protect"/> is a plaintext passthrough and writing here
    /// would put a live credential into Postgres in the clear. The node is left keyless and the
    /// refusal is logged at Error — the one outcome an operator must act on.
    /// </summary>
    RefusedUnprotected,

    /// <summary>The write was attempted and failed (the reason is on the result's detail + the log).</summary>
    WriteFailed,
}

/// <summary>
/// One provider's seed result. <paramref name="ProviderPath"/> is the node path
/// (<c>Provider/{name}</c>), <paramref name="Section"/> the configuration section the key would
/// have come from, and <paramref name="Detail"/> a human-readable note.
///
/// <para>🚨 <paramref name="Detail"/> NEVER carries a key, a fragment of one, or anything derived
/// from one. A key that has been echoed is a key that must be rotated.</para>
/// </summary>
public sealed record ProviderCredentialSeedResult(
    string ProviderPath, string Section, ProviderSeedOutcome Outcome, string? Detail = null);

/// <summary>
/// <b>Deployment configuration is a SEED, not a live source.</b> This is the one seam that carries
/// <c>{Section}:ApiKey</c> / <c>{Section}:Endpoint</c> from a deployment's configuration onto the
/// <c>ModelProvider</c> NODE, so the node — and only the node — can answer at resolve time
/// (MeshWeaver#1982).
///
/// <para>🚨 <b>Why a converging seed and not the create-if-absent one.</b> The catalog's static-repo
/// import marks each <c>Provider/{name}</c> <see cref="SyncBehavior.ExcludeThisAndChildren"/>, so
/// the importer CREATES it once and never revisits it — the claim that protects an admin's key edit
/// from the next boot's re-seed. A key added to the deployment AFTER that node exists therefore
/// reached every factory and never reached the node: measured on <c>memex.systemorph.com</c>, where
/// <c>Provider/Anthropic</c> was created keyless on 2026-08-14, <c>Anthropic__ApiKey</c> was
/// configured after it, and the node stayed keyless until a human pasted the key in on 2026-08-21.
/// A seeder that only runs at CREATION cannot converge that. This one runs on every boot.</para>
///
/// <para><b>Fill-if-absent, per field.</b> The seed writes a field only when the node's own value is
/// empty. An administered value — a key an admin pasted, rotated, or an earlier boot seeded — is
/// never touched, so running twice is not merely safe, it is the point. Nothing here reads its own
/// writes: it takes ONE authoritative snapshot, computes the writes, applies them sequentially and
/// completes (see the reconcile-write-storm rule in <c>Doc/Architecture</c>).</para>
///
/// <para>🚨 <b>Never writes an unprotected key.</b> Whatever is written goes through
/// <see cref="IProviderKeyProtector.Protect"/> and is verified to carry the <c>enc:</c> tag BEFORE
/// the write. With no <c>Ai:KeyProtection:MasterKey</c> configured, Protect is a silent plaintext
/// passthrough — so the seed REFUSES (<see cref="ProviderSeedOutcome.RefusedUnprotected"/>, logged
/// at Error) rather than quietly persisting a live credential in the clear. Checking the produced
/// VALUE rather than probing the master-key provider is deliberate: it is the exact bytes about to
/// be persisted that must be tagged, whichever <see cref="IMasterKeyProvider"/> a hardened
/// deployment plugs in.</para>
///
/// <para><b>Two replicas booting together both run it, and that is fine.</b> Whichever writes first
/// wins; the other reads a keyed node and reports
/// <see cref="ProviderSeedOutcome.AlreadyAdministered"/>. In the narrow window where both decide to
/// write, both values decrypt to the same key — the ciphertexts differ only by nonce — so there is
/// nothing to serialise and no lock to hold.</para>
///
/// <para><b>Where it runs.</b> Only on a deployment that serves the <c>Provider</c> partition from
/// the DB (the static-repo import path) — that is the only place a node can go stale, and the only
/// place there is a node to write. On the in-memory path
/// <see cref="BuiltInLanguageModelProvider"/> projects the live configuration into the served node
/// on every read, so there is nothing to converge and nothing to persist.</para>
/// </summary>
public static class ProviderCredentialSeed
{
    /// <summary>The <c>enc:</c> tag <see cref="IProviderKeyProtector"/> puts on a protected value.</summary>
    private const string ProtectedPrefix = "enc:";

    /// <summary>How long to wait for the provider catalog to be readable before giving up.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when <paramref name="stored"/> is a value <see cref="IProviderKeyProtector"/> has
    /// actually encrypted (rather than passed through untouched because encryption is disabled).
    /// Pure — the master-key check the seed makes before every write.
    /// </summary>
    /// <param name="stored">The value <see cref="IProviderKeyProtector.Protect"/> produced.</param>
    public static bool IsProtected(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The seed decision for ONE provider — pure, so every branch is unit-testable with no hub, no
    /// configuration system and no key material.
    /// </summary>
    /// <param name="node">The provider node's content, or <c>null</c> when the node does not exist.</param>
    /// <param name="hasConfiguredKey">Whether this deployment configures a key for the provider's section.</param>
    /// <param name="protectionAvailable">
    /// Whether the value about to be written is <c>enc:</c>-protected (see <see cref="IsProtected"/>).
    /// Passing the flag rather than the key keeps key material out of this signature entirely.
    /// </param>
    public static ProviderSeedOutcome Decide(
        ModelProviderConfiguration? node, bool hasConfiguredKey, bool protectionAvailable)
    {
        if (!hasConfiguredKey) return ProviderSeedOutcome.NoConfiguredKey;
        if (node is null) return ProviderSeedOutcome.NodeAbsent;
        // Administered data wins — including a key an earlier boot seeded. This is what makes the
        // seed idempotent AND what stops it from ever clobbering a rotation done in the GUI.
        if (!string.IsNullOrWhiteSpace(node.ApiKey)) return ProviderSeedOutcome.AlreadyAdministered;
        if (!protectionAvailable) return ProviderSeedOutcome.RefusedUnprotected;
        return ProviderSeedOutcome.Seeded;
    }

    /// <summary>
    /// The endpoint to seed onto <paramref name="node"/>, or <c>null</c> to leave it alone. Same
    /// fill-if-absent rule as the key (an administered endpoint is never overwritten), minus the
    /// protection requirement — an endpoint is not a credential. Pure.
    /// </summary>
    /// <param name="node">The provider node's content, or <c>null</c> when the node does not exist.</param>
    /// <param name="configuredEndpoint">The deployment's <c>{Section}:Endpoint</c>, if any.</param>
    public static string? EndpointToSeed(ModelProviderConfiguration? node, string? configuredEndpoint) =>
        node is not null
        && string.IsNullOrWhiteSpace(node.Endpoint)
        && !string.IsNullOrWhiteSpace(configuredEndpoint)
            ? configuredEndpoint
            : null;

    /// <summary>
    /// Runs the seed over every registered <see cref="LanguageModelCatalogSource"/>: reads the
    /// provider catalog once, decides per provider, and applies the fill-if-absent writes
    /// SEQUENTIALLY under the System identity (the <c>Provider</c> partition is admin-write, and a
    /// boot seed has no user session). Emits one result per provider; completes when done.
    ///
    /// <para>Reactive end to end — subscribe to run. Never faults: a per-provider failure is
    /// reported as <see cref="ProviderSeedOutcome.WriteFailed"/> so one bad provider cannot stop
    /// the others.</para>
    /// </summary>
    /// <param name="hub">The mesh hub whose services back the catalog, configuration and writes.</param>
    /// <param name="logger">Optional logger; the refusal path logs at Error through it.</param>
    public static IObservable<ProviderCredentialSeedResult> Run(IMessageHub hub, ILogger? logger = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ProviderCredentialSeed");

        var catalog = hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (catalog is null || configuration is null || meshService is null)
            return Observable.Empty<ProviderCredentialSeedResult>();

        // What the deployment actually configures, resolved ONCE up front. Sources with no
        // configured key are dropped here rather than reported per-provider: a deployment that keys
        // nothing in configuration has nothing to seed, and saying so once per boot per provider is
        // log noise, not information.
        var candidates = catalog.Sources
            .Select(s => new
            {
                Source = s,
                // Plain indexer lookups, never GetSection(...).Get<T>(): binding is what throws on a
                // malformed section, and an indexer has nothing to guard.
                ApiKey = Read(configuration, $"{s.SectionName}:ApiKey"),
                Endpoint = Read(configuration, $"{s.SectionName}:Endpoint") ?? s.DefaultEndpoint,
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.ApiKey))
            // Two catalog sources can name the SAME provider node (a legacy section alias); the
            // first registered wins, exactly as the catalog projection resolves it.
            .GroupBy(c => c.Source.ProviderName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        if (candidates.Length == 0)
            return Observable.Empty<ProviderCredentialSeedResult>();

        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var expectedPaths = candidates
            .Select(c => $"{ModelProviderNodeType.RootNamespace}/{c.Source.ProviderName}")
            .ToArray();

        return accessService.RunAsSystem(() => ReadProviders(meshService, expectedPaths, logger)
            .SelectMany(providers => candidates
                .Select(c =>
                {
                    var path = $"{ModelProviderNodeType.RootNamespace}/{c.Source.ProviderName}";
                    providers.TryGetValue(path, out var node);
                    var current = node.ContentAs<ModelProviderConfiguration>(hub.JsonSerializerOptions, logger);

                    // 🚨 Protect FIRST, then decide on the RESULT. What must carry the enc: tag is
                    // the exact value about to be persisted — not a probe, and not an inference from
                    // which IMasterKeyProvider happens to be registered.
                    var protectedKey = protector is null ? c.ApiKey : protector.Protect(c.ApiKey);
                    var outcome = Decide(current, hasConfiguredKey: true, protectionAvailable: IsProtected(protectedKey));
                    var endpoint = EndpointToSeed(current, c.Endpoint);

                    return outcome is ProviderSeedOutcome.Seeded
                        ? Write(hub, path, c.Source.SectionName, protectedKey!, endpoint, logger)
                        : Observable.Return(Report(path, c.Source.SectionName, outcome, logger));
                })
                // SEQUENTIAL. One provider node at a time — a boot seed writes into partitions that
                // may have been created seconds earlier by the import, and a fan-out of cold per-node
                // hub activations is what crashes those writes (the install-path rule).
                .Concat()));
    }

    /// <summary>Configuration indexer read that never throws on a malformed section.</summary>
    private static string? Read(IConfiguration configuration, string key)
    {
        try { return configuration[key]; }
        catch { return null; }
    }

    /// <summary>
    /// ONE authoritative snapshot of the provider catalog, keyed by node path. Polls until every
    /// expected provider node is present (the import that creates them may still be landing), then
    /// proceeds with whatever the last snapshot held — a missing node is reported as
    /// <see cref="ProviderSeedOutcome.NodeAbsent"/>, never waited for forever.
    /// </summary>
    private static IObservable<IReadOnlyDictionary<string, MeshNode>> ReadProviders(
        IMeshService meshService, IReadOnlyList<string> expectedPaths, ILogger? logger)
    {
        var query = $"namespace:{ModelProviderNodeType.RootNamespace} "
                    + $"nodeType:{ModelProviderNodeType.NodeType}";

        IObservable<IReadOnlyDictionary<string, MeshNode>> Snapshot() =>
            meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query, WellKnownUsers.System))
                .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                .Take(1)
                .Select(c => (IReadOnlyDictionary<string, MeshNode>)(c.Items ?? [])
                    .Where(n => !string.IsNullOrEmpty(n.Path))
                    .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

        return Observable.Interval(TimeSpan.FromSeconds(1)).StartWith(0L)
            .SelectMany(_ => Snapshot())
            .Where(m => expectedPaths.All(m.ContainsKey))
            .Take(1)
            .Timeout(QueryTimeout)
            // Not every configured provider necessarily HAS a node (a section configured for a
            // provider this build no longer ships). Fall back to one plain snapshot and report the
            // absences rather than seeding nothing at all.
            .Catch((Exception ex) =>
            {
                logger?.LogInformation(
                    "[ProviderCredentialSeed] not every configured provider node was present within {Timeout}s "
                    + "({Reason}) — seeding against the current snapshot.",
                    QueryTimeout.TotalSeconds, ex.GetType().Name);
                return Snapshot();
            });
    }

    /// <summary>
    /// The fill-if-absent write: <see cref="MeshNodeStreamHandle.Update{TContent}(Func{TContent, TContent})"/>
    /// — the canonical typed verb — re-checking the emptiness under the write so a key an admin set
    /// between the snapshot and here still wins.
    /// </summary>
    private static IObservable<ProviderCredentialSeedResult> Write(
        IMessageHub hub, string path, string section, string protectedKey, string? endpoint, ILogger? logger)
    {
        var seededEndpoint = false;
        return hub.GetWorkspace().GetMeshNodeStream(path)
            .Update<ModelProviderConfiguration>(current =>
            {
                if (!string.IsNullOrWhiteSpace(current.ApiKey))
                    return current;   // raced by an administered write — leave it alone.
                seededEndpoint = endpoint is not null && string.IsNullOrWhiteSpace(current.Endpoint);
                return current with
                {
                    ApiKey = protectedKey,
                    Endpoint = seededEndpoint ? endpoint : current.Endpoint,
                };
            })
            .Take(1)
            .Do(updated =>
                // Force persistence at the per-node hub: sync-protocol updates don't always fire the
                // per-node hub's save subscription for remote-driven changes (same pattern as
                // ModelProviderService.RotateKey).
                hub.Post(new SaveMeshNodeRequest(updated), o => o.WithTarget(new Address(path))))
            .Select(_ =>
            {
                logger?.LogInformation(
                    "[ProviderCredentialSeed] {Path}: seeded the API key from configuration section "
                    + "'{Section}' (encrypted at rest{Endpoint}). The NODE is now the source of truth; "
                    + "rotate it there, not in the deployment configuration.",
                    path, section, seededEndpoint ? ", endpoint seeded too" : string.Empty);
                return new ProviderCredentialSeedResult(path, section, ProviderSeedOutcome.Seeded);
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "[ProviderCredentialSeed] {Path}: could not seed the API key from section '{Section}'.",
                    path, section);
                return Observable.Return(new ProviderCredentialSeedResult(
                    path, section, ProviderSeedOutcome.WriteFailed, ex.Message));
            });
    }

    /// <summary>Logs a non-writing outcome at the level it deserves and returns it.</summary>
    private static ProviderCredentialSeedResult Report(
        string path, string section, ProviderSeedOutcome outcome, ILogger? logger)
    {
        switch (outcome)
        {
            case ProviderSeedOutcome.RefusedUnprotected:
                // 🚨 LOUD. A silent downgrade here means a live provider credential sitting in
                // Postgres in the clear — the failure mode this whole path exists to make impossible.
                logger?.LogError(
                    "[ProviderCredentialSeed] {Path}: REFUSED to seed the API key configured in section "
                    + "'{Section}' because no '{MasterKeyConfigKey}' is configured — provider-key "
                    + "encryption is a plaintext passthrough on this deployment and the seed will not "
                    + "persist a credential in the clear. Set a master key (env "
                    + "'Ai__KeyProtection__MasterKey') and restart; until then this provider stays "
                    + "keyless and its models are reported unusable.",
                    path, section, ConfigMasterKeyProvider.ConfigKey);
                break;
            case ProviderSeedOutcome.NodeAbsent:
                logger?.LogWarning(
                    "[ProviderCredentialSeed] {Path}: no provider node to seed (section '{Section}' "
                    + "configures a key). The catalog import creates it; the next boot seeds it.",
                    path, section);
                break;
            default:
                logger?.LogDebug(
                    "[ProviderCredentialSeed] {Path}: {Outcome} (section '{Section}').",
                    path, outcome, section);
                break;
        }

        return new ProviderCredentialSeedResult(path, section, outcome);
    }
}
