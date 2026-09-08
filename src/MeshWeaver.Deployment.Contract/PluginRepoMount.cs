// One plugin repository a deployment mounts — moved with the Deployment record (see DeploymentContent.cs).
using System;

namespace MeshWeaver.Deployment;

/// <summary>
/// ONE plugin repository a deployment mounts — a registry to consume, or (on the registry
/// installation itself) a git repo to serve. This is the recorded half of what AGENTS.md calls
/// "a module with no sync entry is simply not on the mesh": an instance created without its
/// mounts comes up empty, and the reason is invisible.
///
/// <para>🚨 Records, not secrets — like every other field on a Deployment. A consumer's registry
/// token and a registry's git credential are named through <see cref="SecretName"/> (a Key Vault
/// secret NAME); their values never appear here, because these records sync to git.</para>
/// </summary>
public record PluginRepoMount
{
    /// <summary>
    /// The mount's name. For a consumer it is the registry name (<c>PluginCatalog:Registries:N:Name</c>);
    /// for the registry installation it is the SOURCE name that stamps every package it serves
    /// (<c>Plugins</c>, <c>Education</c>, …) — which is what a pre-install pattern matches on.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Where the packages come from: a registry BASE URL for a consumer
    /// (<c>https://memex.meshweaver.cloud</c>), or a git repository URL when
    /// <see cref="IsRegistrySource"/> — the registry alone holds a git credential.
    /// </summary>
    public string Url { get; init; } = "";

    /// <summary>Git ref, for a registry source. Blank → <c>main</c>. Ignored by a consumer mount.</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// True when this deployment SERVES this repo (it is the registry) rather than consuming it.
    /// The distinction decides which configuration section the mount renders into, and only a
    /// registry mount needs a git credential.
    /// </summary>
    public bool IsRegistrySource { get; init; }

    /// <summary>
    /// The Key Vault secret NAME carrying this mount's credential — a consumer's registry token,
    /// or a registry source's git token. Never a value. Blank → the deployment's default.
    /// </summary>
    public string? SecretName { get; init; }

    /// <summary>Trimmed name, or "" — the form every renderer uses. Pure.</summary>
    public string NormalizedName => (Name ?? "").Trim();

    /// <summary>Trimmed URL with any trailing slash removed, or "". Pure.</summary>
    public string NormalizedUrl => (Url ?? "").Trim().TrimEnd('/');

    /// <summary>Git ref, defaulted to <c>main</c> the way the catalog defaults it. Pure.</summary>
    public string EffectiveRef => string.IsNullOrWhiteSpace(Ref) ? "main" : Ref!.Trim();

    /// <summary>
    /// Why this mount cannot be used, or null when it is usable. A mount without a name or a URL
    /// is not "partially configured" — it silently contributes nothing, which is the failure this
    /// whole record exists to make visible. Pure.
    /// </summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(NormalizedName) ? "a plugin mount needs a name"
        : string.IsNullOrWhiteSpace(NormalizedUrl) ? $"plugin mount '{NormalizedName}' needs a url"
        : !NormalizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? $"plugin mount '{NormalizedName}' must be an https url — got '{NormalizedUrl}'"
        : null;
}
