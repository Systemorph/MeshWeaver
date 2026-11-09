using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a module landing has already READ about a served bundle: that it declares nothing for the
/// NodeType lane to adopt (<see cref="MeshWeaver.Plugin.Packaging.BundleReader.DeclaresNothingToAdopt"/>).
/// Mesh-scoped singleton (never static), keyed by registry, package and version.
///
/// <para>🚨 <b>Why it exists.</b> A fresh install lands every package's module first and installs
/// its nodes after (<c>default-install-modules-first</c>); the node install then asks
/// <see cref="PluginBundleClient.Adopt"/> for the package's prebuilt NodeTypes — the SAME bundle,
/// downloaded a second time to learn "declares no NodeTypes". Measured 2026-11-09 on a local Mac
/// instance: every module-only package fetched twice per pass, ~20 s each, so the duplicate alone
/// doubled the registry time of a boot.</para>
///
/// <para><b>Why remembering is sound.</b> The request is identical — this process's framework
/// identity and architecture, the same advertised version — and a published version describes one
/// tree forever (a module tag is never re-cut with different content). The key ALSO carries the
/// index's per-bundle module identity (<see cref="PluginBundleClient.BundleRef.FrameworkMvid"/>), so
/// a rebuild re-advertised under the same version is a different key and is downloaded; and a fact
/// is trusted for <see cref="Lifetime"/> only — one default-install pass — so a later pass always
/// re-reads the registry. Only a manifest with NO stated miss and NO NodeType assembly is
/// remembered: a package whose NodeTypes the registry could not serve yet states a miss and is
/// never remembered.</para>
/// </summary>
public sealed class BundleOfferingMemo
{
    /// <summary>How long a remembered fact is trusted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private ImmutableDictionary<string, (string Offering, DateTimeOffset At)> facts =
        ImmutableDictionary<string, (string, DateTimeOffset)>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock;

    /// <summary>The memo.</summary>
    /// <param name="clock">The clock; <see cref="TimeProvider.System"/> by default.</param>
    public BundleOfferingMemo(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    /// <summary>Remembers that <paramref name="package"/>@<paramref name="version"/> at
    /// <paramref name="registry"/> (advertised with <paramref name="moduleIdentity"/>) declares nothing to adopt, and what it ships instead.</summary>
    public void Remember(string registry, string package, string version, string? moduleIdentity, string offering) =>
        ImmutableInterlocked.AddOrUpdate(ref facts, Key(registry, package, version, moduleIdentity),
            (offering, clock.GetUtcNow()), (_, _) => (offering, clock.GetUtcNow()));

    /// <summary>What the bundle ships instead of NodeTypes, when a landing read it within
    /// <see cref="Lifetime"/>; otherwise null — download it.</summary>
    public string? NothingToAdopt(string registry, string package, string version, string? moduleIdentity) =>
        facts.TryGetValue(Key(registry, package, version, moduleIdentity), out var fact) && clock.GetUtcNow() - fact.At < Lifetime
            ? fact.Offering
            : null;

    private static string Key(string registry, string package, string version, string? moduleIdentity) =>
        $"{registry.TrimEnd('/')}|{package}|{version}|{moduleIdentity}";
}
