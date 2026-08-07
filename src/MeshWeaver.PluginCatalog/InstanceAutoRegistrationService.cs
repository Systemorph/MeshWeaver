using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The instance key a consumer received when it auto-registered at a registry, persisted so the
/// installation survives restarts without anyone re-copying a token. Lives in the <b>Admin</b>
/// partition (<c>Admin/PluginRegistryCredential/{host-slug}</c>) — platform admins and System only —
/// with the key encrypted at rest via <see cref="IProviderKeyProtector"/> when a master key is
/// configured (the same envelope provider keys use).
/// </summary>
public record PluginRegistryCredential
{
    /// <summary>Base URL of the registry this credential authenticates against.</summary>
    public string RegistryUrl { get; init; } = "";

    /// <summary>The instance id this installation registered under.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>The issued instance key (<c>mwi_…</c>), <c>enc:</c>-protected at rest when a
    /// master key is configured; plaintext passthrough otherwise (same policy as provider keys).</summary>
    public string ProtectedKey { get; init; } = "";

    /// <summary>When the registration happened.</summary>
    public DateTimeOffset RegisteredAt { get; init; }
}

/// <summary>Where auto-registration credentials live and how a registry URL maps to a node id.</summary>
public static class PluginRegistryCredentials
{
    /// <summary>The credential node type.</summary>
    public const string NodeType = "PluginRegistryCredential";

    /// <summary>Namespace (under the Admin partition) holding the credentials.</summary>
    public const string Namespace = "Admin/PluginRegistryCredential";

    /// <summary>The credential node path for a registry base URL — keyed by HOST so the same
    /// registry reached via http/https or with a trailing slash resolves to one credential.</summary>
    public static string Path(string registryUrl)
    {
        var host = Uri.TryCreate((registryUrl ?? "").Trim(), UriKind.Absolute, out var uri)
            ? uri.Host
            : (registryUrl ?? "").Trim();
        var slug = new string(host.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return $"{Namespace}/{(slug.Length == 0 ? "registry" : slug)}";
    }
}

/// <summary>
/// Resolves the token a consumer presents to a registry: an explicitly configured token always
/// wins; otherwise the stored <see cref="PluginRegistryCredential"/> from a first-startup
/// auto-registration, decrypted; otherwise empty (unauthenticated — only an open dev/e2e registry
/// answers). Reads run as System: the credential is deployment infrastructure in the Admin
/// partition, not something the browsing user could (or should) read.
/// </summary>
public sealed class RegistryTokenResolver(IMessageHub hub, ILogger<RegistryTokenResolver> logger)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The effective token for <paramref name="registry"/>. Cold; emits once.</summary>
    public IObservable<string> ResolveToken(PluginRegistryReference registry)
    {
        if (!string.IsNullOrWhiteSpace(registry.Token))
            return Observable.Return(registry.Token.Trim());

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(PluginRegistryCredentials.Path(registry.Url), ReadTimeout))
            .Take(1)
            .Select(node =>
            {
                var credential = ContentAs(node);
                if (credential is null || string.IsNullOrWhiteSpace(credential.ProtectedKey))
                    return "";
                var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
                var raw = protector is null ? credential.ProtectedKey : protector.Unprotect(credential.ProtectedKey);
                if (string.IsNullOrWhiteSpace(raw))
                    logger.LogWarning(
                        "Stored registry credential for {Url} could not be decrypted — was the master key changed?",
                        registry.Url);
                return raw ?? "";
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Reading the stored registry credential for {Url} failed", registry.Url);
                return Observable.Return("");
            });
    }

    private PluginRegistryCredential? ContentAs(MeshNode? node)
    {
        if (node?.Content is null) return null;
        if (node.Content is PluginRegistryCredential typed) return typed;
        if (node.Content is not JsonElement json) return null;
        try { return JsonSerializer.Deserialize<PluginRegistryCredential>(json.GetRawText(), hub.JsonSerializerOptions); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// First-startup auto-registration: when <c>PluginCatalog:BootstrapKey</c> is configured, this
/// installation registers itself at the configured registry on startup — presenting the bootstrap
/// key, receiving its own <c>mwi_</c> instance key — and persists that key as a
/// <see cref="PluginRegistryCredential"/>. With the registry's <c>PluginCatalog:DefaultGrants</c>
/// seeding <c>Plugins/*</c>, a brand-new deployment reaches a filled plugin catalog with nobody
/// copying a token.
///
/// <para>Idempotent and deliberately single-shot per boot: a stored credential (or an explicitly
/// configured token) short-circuits, and a FAILED attempt logs an error and stops — no retry
/// timer. The failure modes are all operator-fixable config (bad/revoked bootstrap key → 401,
/// taken id → 409, wrong URL), and the Plugin Catalog tab makes the unauthenticated state visible;
/// a retry loop would only hide the misconfiguration (AGENTS.md → no watchdogs).</para>
/// </summary>
public sealed class InstanceAutoRegistrationService(
    IMessageHub hub, ILogger<InstanceAutoRegistrationService> logger)
    : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => subscriptions.Dispose();

    private void Start()
    {
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>();
        var bootstrapKey = options?.BootstrapKey?.Trim() ?? "";
        if (bootstrapKey.Length == 0)
            return;

        var registry = options!.EffectiveRegistries.FirstOrDefault();
        if (registry is null)
        {
            logger.LogError(
                "PluginCatalog:BootstrapKey is set but no registry URL is configured — nothing to register at.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(registry.Token))
        {
            logger.LogInformation(
                "PluginCatalog:BootstrapKey is set but a registry token is already configured — the "
                + "explicit token wins; skipping auto-registration.");
            return;
        }
        var instanceId = options.InstanceId?.Trim() ?? "";
        if (instanceId.Length == 0)
        {
            logger.LogError(
                "PluginCatalog:BootstrapKey is set but PluginCatalog:InstanceId is not. The instance "
                + "id is a stable global identity and is never derived from a machine or pod name — "
                + "set it explicitly.");
            return;
        }

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var client = hub.ServiceProvider.GetRequiredService<InstanceRegistrationClient>();
        var credentialPath = PluginRegistryCredentials.Path(registry.Url);

        subscriptions.Add(Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(credentialPath, TimeSpan.FromSeconds(10)))
            .Take(1)
            .SelectMany(existing =>
            {
                if (existing is not null)
                {
                    logger.LogDebug("Registry credential already stored at {Path} — no registration needed.",
                        credentialPath);
                    return Observable.Return(Unit.Default);
                }

                return client.Register(registry.Url, new InstanceRegistrationPayloads.Request(
                        bootstrapKey, instanceId,
                        DisplayName: options.InstanceName?.Trim() is { Length: > 0 } name ? name : instanceId,
                        HomeUrl: options.HomeUrl?.Trim() ?? ""))
                    .SelectMany(result =>
                    {
                        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
                        var node = new MeshNode(
                            credentialPath.Split('/').Last(), PluginRegistryCredentials.Namespace)
                        {
                            Name = $"Registry credential: {registry.Url}",
                            NodeType = PluginRegistryCredentials.NodeType,
                            State = MeshNodeState.Active,
                            Content = new PluginRegistryCredential
                            {
                                RegistryUrl = registry.Url.TrimEnd('/'),
                                InstanceId = result.InstanceId,
                                ProtectedKey = protector?.Protect(result.InstanceKey) ?? result.InstanceKey,
                                RegisteredAt = DateTimeOffset.UtcNow,
                            },
                        };
                        return Observable.Defer(() =>
                        {
                            var write = accessService.ImpersonateAsSystem();
                            return meshService.CreateNode(node)
                                .Select(_ =>
                                {
                                    logger.LogInformation(
                                        "Auto-registered this installation as instance '{InstanceId}' at {Url}; "
                                        + "the issued key is stored at {Path}.",
                                        result.InstanceId, registry.Url, credentialPath);
                                    return Unit.Default;
                                })
                                .Finally(() => write.Dispose());
                        });
                    })
                    // Concurrent replicas race this whole block: both read "no credential", one
                    // registers first, and the loser sees 409 (the id is taken) — or, narrower, the
                    // credential write collides. In BOTH cases the deployment as a whole is fine if
                    // a credential now exists; converge on it instead of erroring a healthy pod.
                    // A 409 with NO stored credential is the real misconfiguration (the id belongs
                    // to someone else) and still propagates.
                    .Catch((Exception ex) => Observable.Using(
                            () => accessService.ImpersonateAsSystem(),
                            _ => hub.GetMeshNode(credentialPath, TimeSpan.FromSeconds(10)))
                        .Take(1)
                        .SelectMany(stored =>
                        {
                            if (stored is null)
                                return Observable.Throw<Unit>(ex);
                            logger.LogInformation(
                                "Another replica completed the auto-registration first; using the "
                                + "credential stored at {Path}.", credentialPath);
                            return Observable.Return(Unit.Default);
                        }));
            })
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "First-startup instance auto-registration at {Url} failed. Fix the configuration "
                    + "(401 = invalid/revoked bootstrap key, 409 = instance id already taken) and "
                    + "restart; no retry is attempted.", registry.Url)));
    }
}
