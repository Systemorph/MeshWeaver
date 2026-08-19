using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The registry's ACQUISITION half of the module lane (#1664 step 13) — how a module bundle built
/// by the repo that owns it gets onto the registry, so the registry can serve it onward.
///
/// <para><b>Why this exists.</b> Every other link was already built: a satellite repo packs a
/// bundle (<c>module-pack</c>), a package declares <c>content.module</c>, a consumer fetches
/// <c>/api/plugins/bundles</c>, gates it on the platform floor and lands it into
/// <c>modules/</c> (restart-as-activation). But a registry serves only what IT runs
/// (<see cref="ModuleBundleSource"/> reads its own <c>modules/</c>), and that folder is written by
/// the platform image's publish layout. So a module whose source has LEFT the platform repo can be
/// packed, declared and installed — and still reach nobody, because the bytes have no way onto the
/// registry in the first place. That is the whole gap this closes.</para>
///
/// <para>Pure decisions only — no HTTP, no mesh, no disk — so the authorization and acceptance
/// rules are pinnable in unit tests, the way <see cref="ModuleBundleSource"/>'s serve rules are.
/// The route wires these to <c>ModuleLandingService</c>, which re-checks the floor and refuses the
/// same-identity trap-door at placement.</para>
/// </summary>
public static class ModulePublish
{
    /// <summary>
    /// Configuration key holding the publish token. 🚨 When it is unset the route is NOT MAPPED at
    /// all — the same shape <c>LogWatch:IngestToken</c> uses for its ingest route. An unconfigured
    /// registry therefore has no publish surface to attack, rather than one that answers 401.
    /// </summary>
    public const string TokenConfigKey = "Plugins:Registry:PublishToken";

    /// <summary>An accepted upload, ready for <c>ModuleLandingService.LandModule</c>.</summary>
    /// <param name="Module">Entry-assembly name without extension — the <c>modules/&lt;name&gt;/</c> folder.</param>
    /// <param name="Version">The package version these bytes were packed at, recorded on the activation entry.</param>
    /// <param name="MinMeshVersion">The module's declared platform floor, re-checked at placement.</param>
    /// <param name="FrameworkMvid">The framework build the producer compiled against — diagnostic only.</param>
    /// <param name="Files">The module's closure: file name + bytes, entry DLL included.</param>
    public sealed record Accepted(
        string Module,
        string? Version,
        string? MinMeshVersion,
        string? FrameworkMvid,
        IReadOnlyList<(string FileName, byte[] Bytes)> Files);

    /// <summary>
    /// Why <paramref name="authorizationHeader"/> may not publish, or null when it may.
    ///
    /// <para>🚨 Publishing is NOT the instance-key grant that guards the read routes. A read grant
    /// says which packages an instance may PULL; writing bytes that every consumer will then load
    /// is a different privilege, held by the CI of the repo that owns the module. Compared in
    /// constant time — a publish token that leaks one byte per request is a publish token.</para>
    /// </summary>
    public static string? DeclineAuthorization(string? configuredToken, string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
            // Fail closed. The route should not be mapped at all in this state; if it ever is,
            // "no token configured" must never mean "anyone may publish".
            return "module publishing is not configured on this instance";

        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "a publish token is required (Authorization: Bearer …)";

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(configuredToken.Trim());
        return CryptographicOperations.FixedTimeEquals(presented, expected)
            ? null
            : "the presented publish token is not valid";
    }

    /// <summary>
    /// Whether an uploaded bundle may be landed for <paramref name="plugin"/>: exactly one of
    /// <c>Accepted</c> / <c>DeclineReason</c> is meaningful.
    ///
    /// <para>The checks are the ones that cannot be recovered later: a bundle landed under the
    /// WRONG package id would be served to that package's consumers, and a file name carrying a
    /// path would escape <c>modules/&lt;name&gt;/</c> when written. The platform floor and the
    /// same-identity trap-door stay <c>ModuleLandingService</c>'s to enforce — one owner each.</para>
    /// </summary>
    public static (Accepted? Accepted, string? DeclineReason) Validate(
        string plugin,
        BundleReader.Manifest? manifest,
        IReadOnlyList<BundleReader.ModuleFile> files,
        string? version = null)
    {
        if (string.IsNullOrWhiteSpace(plugin))
            return (null, "no package id was named");
        if (manifest is null)
            return (null, "the upload carries no bundle manifest");

        // The bundle says which package it belongs to; the URL says where it is being filed. A
        // mismatch is never a harmless mislabel — the registry would serve these bytes to the OTHER
        // package's consumers, who asked for something else entirely.
        if (!string.IsNullOrWhiteSpace(manifest.Plugin)
            && !string.Equals(manifest.Plugin, plugin, StringComparison.OrdinalIgnoreCase))
            return (null,
                $"the bundle declares package '{manifest.Plugin}' but was published as '{plugin}'");

        if (manifest.Module?.AssemblyName is not { Length: > 0 } module)
            return (null, "the bundle declares no module (content.module / --plugin packed without one)");

        if (module is "." or ".."
            || module.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || module.Contains('/') || module.Contains('\\'))
            return (null, $"'{module}' is not a valid module name");

        if (files.Count == 0)
            return (null, $"the bundle carries no files for module '{module}'");

        foreach (var file in files)
        {
            // These names become paths under modules/<module>/. A separator or a traversal segment
            // is a write outside the folder, so it is refused HERE, before any of it reaches disk.
            if (string.IsNullOrWhiteSpace(file.FileName)
                || file.FileName is "." or ".."
                || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || file.FileName.Contains('/') || file.FileName.Contains('\\'))
                return (null, $"'{file.FileName}' is not a valid file name in a module bundle");
        }

        var entry = module + ".dll";
        if (!files.Any(f => string.Equals(f.FileName, entry, StringComparison.OrdinalIgnoreCase)))
            return (null, $"the bundle carries no entry assembly '{entry}'");

        return (new Accepted(
            module,
            string.IsNullOrWhiteSpace(version) ? manifest.Version : version,
            manifest.Module.MinMeshVersion,
            manifest.FrameworkMvid,
            [.. files.Select(f => (f.FileName, f.Bytes))]), null);
    }
}
