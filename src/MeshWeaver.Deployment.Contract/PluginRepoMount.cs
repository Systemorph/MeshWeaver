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
    /// The Key Vault secret NAME carrying this mount's credential. Never a value. Blank → the
    /// deployment's default identity.
    ///
    /// <para>On a <see cref="IsRegistrySource"/> mount this is the token the git repository is READ
    /// with (a fine-grained PAT, or any token GitHub accepts over https) instead of the instance's
    /// GitHub App: the renderers emit <c>PluginCatalog:Sources:N:TokenKey</c> =
    /// <see cref="SourceTokenConfigKey"/> and map this vault object onto
    /// <see cref="SourceTokenEnvKey"/>, and the portal reads the repository with the value it finds
    /// there — refusing by name, never falling back, when it finds none.</para>
    /// </summary>
    public string? SecretName { get; init; }

    /// <summary>
    /// The account (organisation or user login) whose installation of the instance's GitHub App
    /// reads this git repository — for a <see cref="IsRegistrySource"/> mount in ANOTHER
    /// organisation than the instance's own installation. Rendered as
    /// <c>PluginCatalog:Sources:N:InstallationOwner</c>; the portal mints (and caches) an
    /// installation token per owner. Blank → the instance's own installation. Mutually exclusive
    /// with <see cref="SecretName"/>: a source has ONE credential.
    /// </summary>
    public string? InstallationOwner { get; init; }

    /// <summary>Trimmed name, or "" — the form every renderer uses. Pure.</summary>
    public string NormalizedName => (Name ?? "").Trim();

    /// <summary>Trimmed URL with any trailing slash removed, or "". Pure.</summary>
    public string NormalizedUrl => (Url ?? "").Trim().TrimEnd('/');

    /// <summary>Git ref, defaulted to <c>main</c> the way the catalog defaults it. Pure.</summary>
    public string EffectiveRef => string.IsNullOrWhiteSpace(Ref) ? "main" : Ref!.Trim();

    /// <summary>Trimmed <see cref="SecretName"/>, or null when blank. Pure.</summary>
    public string? NormalizedSecretName => string.IsNullOrWhiteSpace(SecretName) ? null : SecretName!.Trim();

    /// <summary>Trimmed <see cref="InstallationOwner"/>, or null when blank. Pure.</summary>
    public string? NormalizedInstallationOwner =>
        string.IsNullOrWhiteSpace(InstallationOwner) ? null : InstallationOwner!.Trim();

    /// <summary>
    /// The configuration section a git source's vault-delivered token lands in — one key per
    /// source NAME, so a reordered mount list never moves a credential onto another repository
    /// (which an index-aligned key would).
    /// </summary>
    public const string SourceTokensSection = "PluginCatalog:SourceTokens";

    /// <summary>
    /// The mount's name as ONE configuration-key segment: every run of characters that is not a
    /// letter or digit becomes a single <c>_</c> (<c>:</c> and <c>__</c> are separators in a
    /// configuration key and an environment variable). Pure.
    /// </summary>
    public string KeySegment
    {
        get
        {
            var builder = new System.Text.StringBuilder();
            foreach (var c in NormalizedName)
            {
                if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
                else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
            }
            return builder.ToString().Trim('_');
        }
    }

    /// <summary>
    /// The CONFIGURATION key this source's token is read from
    /// (<c>PluginCatalog:SourceTokens:{name}</c>) — the value of
    /// <c>PluginCatalog:Sources:N:TokenKey</c> — or null when the mount is not a registry source
    /// naming a <see cref="SecretName"/>. Pure.
    /// </summary>
    public string? SourceTokenConfigKey =>
        IsRegistrySource && NormalizedSecretName is not null ? $"{SourceTokensSection}:{KeySegment}" : null;

    /// <summary>
    /// <see cref="SourceTokenConfigKey"/> in its environment spelling
    /// (<c>PluginCatalog__SourceTokens__{name}</c>) — the key the vault object
    /// <see cref="SecretName"/> is mapped onto. Null when there is no token key. Pure.
    /// </summary>
    public string? SourceTokenEnvKey => SourceTokenConfigKey?.Replace(":", "__");

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
        : NormalizedInstallationOwner is not null && !IsRegistrySource
            ? $"plugin mount '{NormalizedName}' names installationOwner '{NormalizedInstallationOwner}' but is "
              + "not a registry source — only a git source is read with a GitHub App installation"
        : NormalizedInstallationOwner is not null && NormalizedSecretName is not null
            ? $"plugin mount '{NormalizedName}' names both secretName and installationOwner — a git "
              + "source is read with ONE credential: a vault token or an App installation"
        : IsRegistrySource && NormalizedSecretName is not null && KeySegment.Length == 0
            ? $"plugin mount '{NormalizedName}' names secretName but its name has no letter or digit "
              + "to key the token by"
        : null;
}
