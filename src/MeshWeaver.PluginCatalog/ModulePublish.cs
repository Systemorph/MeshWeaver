using System.Linq;
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
    /// <param name="FrameworkMvid">
    /// The framework build the producer compiled against.
    ///
    /// <para>🚨 <b>NOT diagnostic</b> — that word was left here when #3154 merged and it is exactly
    /// how an optional field stays optional. <c>ModuleUpdateDecision.Decide</c> reads this value
    /// back (shelf → <c>ShelveModule</c> → the index's per-bundle <c>frameworkMvid</c>) and compares
    /// <b>(version, framework identity)</b> on every installation's every reconcile: a module's
    /// version encodes CONTENT only, so a rebuild of unchanged source against a new platform
    /// republishes under the same version and the identity is the only thing that tells that
    /// rebuild from a no-op.</para>
    ///
    /// <para>Null here would therefore be a permanent skip-and-say-so for every consumer of the
    /// bundle — an unknown on the SERVED side cannot be healed by landing, only an unknown on the
    /// landed side can. #3211 closes that where it is created (the packer refuses to write such a
    /// bundle, the lane refuses to POST one) and, since #3240, <see cref="Validate"/> refuses it
    /// HERE too, as a named 400. So this value is never null on an accepted upload.</para>
    /// </param>
    /// <param name="Files">The module's closure: file name + bytes, entry DLL included.</param>
    public sealed record Accepted(
        string Module,
        string? Version,
        string? MinMeshVersion,
        string? FrameworkMvid,
        IReadOnlyList<(string FileName, byte[] Bytes)> Files,
        string? PackagePath = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? StaticAssets = null);

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
        string? version = null,
        string? packagePath = null,
        IReadOnlyList<BundleReader.ModuleAsset>? staticAssets = null)
    {
        // The package path ("Plugins/AzureBlob") is what stamps the landed entry's SOURCE — the
        // key every PluginGrant and serve-side filter matches on. Optional (an older publisher
        // simply lands an unstamped entry, servable to nobody until re-published), but when
        // given it must be a two-segment source/plugin path whose plugin half matches the URL.
        if (!string.IsNullOrWhiteSpace(packagePath))
        {
            var segments = packagePath.Split('/');
            if (segments.Length != 2
                || segments.Any(seg => string.IsNullOrWhiteSpace(seg)
                    || seg is "." or ".."
                    || seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || !string.Equals(segments[1], plugin, StringComparison.OrdinalIgnoreCase))
                return (null,
                    $"'{packagePath}' is not a valid package path (expected <source>/{plugin})");
        }
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

        // 🚨 ARMED 2026-09-05 (#3240) — the LAST refusal of #3211, and the registry's own.
        //
        // A bundle stating no framework identity shelves a null, the index advertises a null, and
        // ModuleUpdateDecision (#3154) then answers "already landed — the identity could not be
        // checked" for this module on every reconcile of every installation, FOREVER: an unknown on
        // the SERVED side is the one landing cannot heal. The producer already refuses to pack or
        // POST such a bundle, but a registry that trusts its producers is not a registry — this is
        // the check that does not depend on which lane, which pin, or which repo sent the bytes.
        //
        // 🚨 IT WAS DELIBERATELY NOT ARMED UNTIL BOTH HALVES OF #3240'S CRITERION WERE MEASURED,
        // because arming it early takes the fleet's publishes down instead of the nulls (measured
        // 2026-09-03: MeshWeaver.Plugins run 33773265959 packed all 34 bundles "built-against MVID
        // (unrecorded)"). Both halves, measured 2026-09-05:
        //
        //   1. every repo that publishes modules pins node-repo-module-pack.yml PAST #3237
        //      (da2bb12d3, 09-03 17:49Z) — MeshWeaver.Plugins at c41a34fda (09-04 05:57Z) and
        //      MeshWeaver.SocialMedia at fec69fc66 (09-03 21:27Z); Education and Reinsurance do not
        //      call the lane at all;
        //   2. a full publish wave from EACH completed since, `publish: true`, stating an identity:
        //      Plugins run 33941672487 (09-05 03:31Z) "built against: g7d644de95…" and SocialMedia
        //      run 33941835795 (09-05 03:27Z) "built against: cef92e9759…". Both publish jobs
        //      succeeded, so the producer-side refusal never fired.
        //
        // Both token shapes are fine here and the check must stay shape-AGNOSTIC: an image-pinned
        // lane states `g<sha>`, a from-source lane a 32-hex MVID, and FrameworkIdentity documents a
        // third (`s<hash>`). The only thing that is never acceptable is ABSENCE — a value this
        // registry would shelve as null.
        if (string.IsNullOrWhiteSpace(manifest.FrameworkMvid))
            return (null,
                $"the bundle states no framework identity (manifest frameworkMvid is absent), so "
                + $"the registry would shelve a null for '{module}' and advertise a null on the "
                + "index — and every consumer would then answer 'already landed, the identity "
                + "could not be checked' on every reconcile, forever (#3154, #3211). Its producer "
                + "packs on a lane older than MeshWeaver#3211: bump that repo's "
                + "node-repo-module-pack.yml pin and re-publish.");

        return (new Accepted(
            module,
            string.IsNullOrWhiteSpace(version) ? manifest.Version : version,
            manifest.Module.MinMeshVersion,
            manifest.FrameworkMvid,
            [.. files.Select(f => (f.FileName, f.Bytes))],
            string.IsNullOrWhiteSpace(packagePath) ? null : packagePath,
            staticAssets is { Count: > 0 }
                ? [.. staticAssets.Select(a => (a.RelativePath, a.Bytes))]
                : null), null);
    }
}
