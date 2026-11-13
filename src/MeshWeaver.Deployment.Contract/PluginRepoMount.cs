// One plugin repository a deployment mounts — moved with the Deployment record (see DeploymentContent.cs).
using System;
using System.Collections.Immutable;
using System.Linq;

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
    /// 🚨 The ref this registry source is served at PER PLATFORM LINE — major → ref, e.g.
    /// <c>{ "3": "main3" }</c> (policy <c>major-lines-run-separately</c>,
    /// Doc/Architecture/MajorLinesRunSeparately §4b). A caller on line 3 is listed and served the
    /// repository at <c>main3</c>; a caller on any line this map does not name is served
    /// <see cref="EffectiveRef"/>, exactly as before. Empty (the default) renders nothing, so a record
    /// that declares none keeps today's single-ref behaviour byte for byte.
    /// <para>Declared, never derived from a branch-naming convention: a repository that has no
    /// <c>main3</c> must not be asked for one. Only a registry source carries it
    /// (<see cref="Problem"/> refuses it on a consumer mount, whose line is its own record's).</para>
    /// </summary>
    public ImmutableSortedDictionary<string, string> LineRefs { get; init; }
        = ImmutableSortedDictionary<string, string>.Empty;

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

    /// <summary>
    /// A stated platform line in its ONE canonical form — a non-negative integer without leading
    /// zeros (<c>" 03 "</c> → <c>"3"</c>), or null for anything else (blank, <c>*</c>, a word). Every
    /// reader of a line goes through this rule — <see cref="RefForLine"/>, <see cref="NormalizedLineRefs"/>,
    /// <see cref="Problem"/> and the portal's <c>PlatformLine.Normalize</c> — so no two of them can
    /// disagree about which line a key names. Pure.
    /// </summary>
    /// <param name="line">The stated line.</param>
    /// <returns>The canonical line, or null.</returns>
    public static string? NormalizeLine(string? line)
    {
        var s = (line ?? "").Trim();
        return s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// <see cref="LineRefs"/> keyed by canonical line (<see cref="NormalizeLine"/>), refs trimmed, in
    /// line order — the form every renderer uses. TOTAL: an entry whose line is not a number or whose
    /// ref is blank is dropped, and of two keys naming the same line (<c>"3"</c>, <c>" 3 "</c>,
    /// <c>"03"</c>) the first in ordinal key order is kept — never an exception. Such a record is
    /// refused by name by <see cref="Problem"/>, so a usable mount never reaches either case. Pure.
    /// </summary>
    public ImmutableSortedDictionary<string, string> NormalizedLineRefs
    {
        get
        {
            var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in LineRefs ?? ImmutableSortedDictionary<string, string>.Empty)
                if (NormalizeLine(key) is { } line && !string.IsNullOrWhiteSpace(value) && !builder.ContainsKey(line))
                    builder[line] = value.Trim();
            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// The ref a caller on <paramref name="line"/> is served: its <see cref="NormalizedLineRefs"/>
    /// entry, the line canonicalised by <see cref="NormalizeLine"/>, else <see cref="EffectiveRef"/>. Pure.
    /// </summary>
    /// <param name="line">The caller's platform major (<c>"3"</c>), or null when unstated.</param>
    /// <returns>The ref to read the repository at.</returns>
    public string RefForLine(string? line) =>
        NormalizeLine(line) is { } canonical && NormalizedLineRefs.TryGetValue(canonical, out var lineRef)
            ? lineRef
            : EffectiveRef;

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
    public string KeySegment => KeySegmentOf(NormalizedName);

    /// <summary>
    /// <paramref name="name"/> as ONE configuration-key segment — the rule <see cref="KeySegment"/>
    /// applies, exposed so the portal reading a name-keyed section (<c>PluginCatalog:LineRefs</c>)
    /// keys a source exactly as the renderer did. Pure.
    /// </summary>
    /// <param name="name">A source name.</param>
    /// <returns>The key segment, possibly empty.</returns>
    public static string KeySegmentOf(string? name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in (name ?? "").Trim())
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }
        return builder.ToString().Trim('_');
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
        : LineRefsProblem();

    /// <summary>Why <see cref="LineRefs"/> cannot be used, or null. Pure.</summary>
    private string? LineRefsProblem()
    {
        var declared = LineRefs ?? ImmutableSortedDictionary<string, string>.Empty;
        if (declared.Count == 0)
            return null;
        if (!IsRegistrySource)
            return $"plugin mount '{NormalizedName}' names lineRefs but is not a registry source — a consumer "
                   + "is served its own record's line by the registry, it does not choose a ref";
        if (KeySegment.Length == 0)
            return $"plugin mount '{NormalizedName}' names lineRefs but its name has no letter or digit to key them by";
        var seen = ImmutableDictionary<string, string>.Empty;
        foreach (var (line, lineRef) in declared)
        {
            if (NormalizeLine(line) is not { } l)
                return $"plugin mount '{NormalizedName}' names lineRefs entry '{line}' — a line is a platform "
                       + "major (\"3\", \"4\")";
            if (seen.TryGetValue(l, out var first))
                return $"plugin mount '{NormalizedName}' names line {l} twice in lineRefs ('{first}' and '{line}') "
                       + "— declare each line once";
            seen = seen.Add(l, line);
            if (string.IsNullOrWhiteSpace(lineRef))
                return $"plugin mount '{NormalizedName}' names line {l} with no ref";
        }
        return null;
    }
}
