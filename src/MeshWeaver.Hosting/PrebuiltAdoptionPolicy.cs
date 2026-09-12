using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting;

/// <summary>
/// How closely a prebuilt bundle's platform must match the RUNNING platform before this instance
/// adopts its bytes — the <c>Modules:VersionStrictness</c> setting (maintainer directive,
/// 2026-09-08: <i>"typically it should accept newer platform versions, especially within the same
/// family ⇒ a setting for version strictness; for dev we should be very tolerant and only use min
/// versions"</i>).
/// </summary>
public enum VersionStrictness
{
    /// <summary>
    /// Today's rule: only a bundle sealed under THIS process's exact framework identity is adopted.
    /// Every platform roll therefore adopts nothing until every satellite has re-sealed for the
    /// new identity — the shape that blocked the 2026-09-08 roll.
    /// </summary>
    Exact = 1,

    /// <summary>
    /// The default: a bundle sealed for another identity of the SAME MAJOR LINE (3.x on 3.y) is
    /// adopted when its declared floor is satisfied AND its type links resolve against the running
    /// platform (<see cref="ModulePlatformLink"/>, measured — never a version comparison alone).
    /// A bundle whose links do not resolve is compiled from source instead, or refused loudly on a
    /// <c>Modules:RequirePrebuilt</c> mesh.
    /// </summary>
    Family = 2,

    /// <summary>
    /// The development default: ANY sealed bundle is adopted when its floor is satisfied and its
    /// links resolve — the platform line is not consulted. Tolerant on purpose: a developer runs
    /// an unreleased platform and wants yesterday's bakes, not a rebuild of every module.
    /// </summary>
    Minimum = 3,
}

/// <summary>What the policy decided for one bundle.</summary>
public enum AdoptionVerdict
{
    /// <summary>Adopt the bundle's bytes — subject to the per-type link check when
    /// <see cref="AdoptionDecision.LinkCheckRequired"/> says so.</summary>
    Adopt = 1,

    /// <summary>Do not adopt; nothing else changes (the sweep compiles, as today).</summary>
    Decline = 2,

    /// <summary>The bytes cannot load here (a measured link is missing): compile the live source
    /// instead — on a mesh that may compile.</summary>
    CompileInstead = 3,

    /// <summary>The bytes cannot load here AND this mesh does not compile module content
    /// (<c>Modules:RequirePrebuilt</c>): refused, loudly, naming what is missing.</summary>
    Refuse = 4,
}

/// <summary>
/// A decision of <see cref="PrebuiltAdoptionPolicy"/>: the verdict, the sentence an operator
/// reads, and whether the per-type measured link check still has to run before the bytes land.
/// </summary>
/// <param name="Verdict">What to do.</param>
/// <param name="Reason">Why, naming both sides — never "declined" alone.</param>
/// <param name="LinkCheckRequired">True when the bundle was NOT sealed for this exact identity and
/// each assembly must prove its type references resolve against the running platform before it is
/// adopted (<see cref="PrebuiltAdoptionPolicy.AfterLink"/>).</param>
public sealed record AdoptionDecision(AdoptionVerdict Verdict, string Reason, bool LinkCheckRequired)
{
    /// <summary>True when the bytes may land (possibly after the link check).</summary>
    public bool Adopts => Verdict == AdoptionVerdict.Adopt;
}

/// <summary>
/// 🚨 <b>The ONE decision for "may this instance adopt a prebuilt bundle sealed for a platform
/// that is not exactly the one running".</b> Pure; every I/O-bearing caller
/// (<see cref="ShippedPrebuiltBundles"/> at boot and at install) hands it the same shape.
///
/// <para><b>Why a version comparison is never the whole answer.</b> A NodeType assembly is
/// compiled in-process against the platform's reference assemblies, so its REAL requirement is
/// the set of types its bytes link against. A semver floor is a claim; the link check
/// (<see cref="ModulePlatformLink.Check(byte[], string, IReadOnlySet{string}, ModulePlatformSurface)"/>)
/// is a measurement over the assembly's own metadata — the same instrument the module lane uses
/// (<c>Doc/Architecture/ModulePlatformLinkGate</c>). So under <see cref="VersionStrictness.Family"/>
/// and <see cref="VersionStrictness.Minimum"/> the policy says <i>"adopt, if the links resolve"</i>
/// and <see cref="AfterLink"/> turns the measurement into the final verdict.</para>
///
/// <para><b>What the link check does NOT see, stated so nobody assumes it does:</b> a member that
/// moved on a type that still exists. That shape surfaces at activation as a
/// <c>MissingMethodException</c>, and the activation path's stale-build self-heal recompiles the
/// type from source — the same fallback a bundle declined here takes, one step later.</para>
///
/// <para><b>Defaults.</b> <see cref="VersionStrictness.Family"/> everywhere; <see cref="VersionStrictness.Minimum"/>
/// when the host says it is a Development environment (the Monolith and the Aspire dev profiles
/// set <c>ASPNETCORE_ENVIRONMENT=Development</c>). A configured <see cref="ConfigKey"/> always
/// wins over the environment.</para>
/// </summary>
public static class PrebuiltAdoptionPolicy
{
    /// <summary>
    /// The configuration key: <c>Exact</c> | <c>Family</c> | <c>Minimum</c> (case-insensitive).
    /// Documented beside <see cref="PrebuiltAssemblySeeder.RequirePrebuiltConfigKey"/> in
    /// <c>Doc/Architecture/ModuleVersioning</c>.
    /// </summary>
    public const string ConfigKey = "Modules:VersionStrictness";

    /// <summary>One bundle as the policy sees it.</summary>
    /// <param name="FrameworkIdentity">The framework identity the bundle's manifest records.</param>
    /// <param name="PlatformVersion">The platform version that identity was published under
    /// (<c>_releases/&lt;version&gt;</c>), or null when no release marker names it.</param>
    /// <param name="MinMeshVersion">The bundle's declared platform floor, or null for none.</param>
    public sealed record Candidate(string? FrameworkIdentity, string? PlatformVersion, string? MinMeshVersion);

    /// <summary>The running process as the policy sees it.</summary>
    /// <param name="FrameworkIdentity">This process's framework identity
    /// (<see cref="PrebuiltAssemblySeeder.LiveFrameworkMvid"/>).</param>
    /// <param name="PlatformVersion">This process's platform version, build metadata stripped.</param>
    /// <param name="RequirePrebuilt">Whether this mesh refuses to compile module content.</param>
    public sealed record Live(string FrameworkIdentity, string? PlatformVersion, bool RequirePrebuilt);

    /// <summary>The running platform's version with the <c>+sha</c> build metadata stripped —
    /// the same reading <c>ModulePlatformFloor.RunningVersion</c> takes, so the two lanes never
    /// disagree about what version is running.</summary>
    public static string? RunningPlatformVersion
    {
        get
        {
            var version = PlatformBuildInfo.PlatformVersion;
            if (string.IsNullOrWhiteSpace(version) || version == "unknown")
                return null;
            var plus = version.IndexOf('+');
            return plus < 0 ? version : version[..plus];
        }
    }

    /// <summary>The live side as this process resolves it. Never throws.</summary>
    public static Live LiveOf(IServiceProvider? services) =>
        new(PrebuiltAssemblySeeder.LiveFrameworkMvid, RunningPlatformVersion,
            PrebuiltAssemblySeeder.RequirePrebuilt(services));

    /// <summary>
    /// Resolves the strictness for this process: the configured value when present, else the
    /// environment default (<see cref="DefaultFor"/>). Never throws.
    /// </summary>
    public static VersionStrictness Resolve(IServiceProvider? services)
    {
        string? configured = null;
        var development = false;
        try
        {
            configured = services?.GetService<IConfiguration>()?[ConfigKey];
            development = services?.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        }
        catch
        {
            // Fail towards the general default, never towards a crash in a boot path.
        }
        return Parse(configured, development);
    }

    /// <summary>The pure form of <see cref="Resolve"/>: a configured value wins; absent or
    /// unparseable falls back to <see cref="DefaultFor"/>.</summary>
    public static VersionStrictness Parse(string? configured, bool isDevelopment) =>
        Enum.TryParse<VersionStrictness>(configured?.Trim(), ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : DefaultFor(isDevelopment);

    /// <summary><see cref="VersionStrictness.Minimum"/> for a Development host, else
    /// <see cref="VersionStrictness.Family"/>.</summary>
    public static VersionStrictness DefaultFor(bool isDevelopment) =>
        isDevelopment ? VersionStrictness.Minimum : VersionStrictness.Family;

    /// <summary>The major line of a version string (<c>3.0.0-ci.8059</c> → 3), or null when the
    /// string does not start with one.</summary>
    public static int? MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var s = version.Trim();
        var end = 0;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
            end++;
        return end > 0 && int.TryParse(s[..end], out var major) ? major : null;
    }

    /// <summary>True when both versions are known and share a major line.</summary>
    public static bool SameFamily(string? a, string? b) =>
        MajorOf(a) is { } x && MajorOf(b) is { } y && x == y;

    /// <summary>
    /// The identity/version/floor half of the decision — no bytes read. A bundle sealed for
    /// THIS identity adopts under every strictness with no link check (the exact path, byte for
    /// byte what shipped before this policy existed).
    /// </summary>
    public static AdoptionDecision Decide(VersionStrictness strictness, Candidate candidate, Live live)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(live);

        if (!string.IsNullOrEmpty(candidate.FrameworkIdentity)
            && string.Equals(candidate.FrameworkIdentity, live.FrameworkIdentity, StringComparison.Ordinal))
            return new AdoptionDecision(AdoptionVerdict.Adopt,
                $"sealed for this framework identity {live.FrameworkIdentity}", LinkCheckRequired: false);

        if (strictness == VersionStrictness.Exact)
            return new AdoptionDecision(AdoptionVerdict.Decline,
                PrebuiltAssemblySeeder.DeclineReason(candidate.FrameworkIdentity, live.FrameworkIdentity)
                ?? "not this framework identity", LinkCheckRequired: false);

        if (string.IsNullOrEmpty(candidate.FrameworkIdentity))
            return new AdoptionDecision(AdoptionVerdict.Decline,
                "the producer recorded no framework identity, so the bundle cannot be placed on any "
                + "platform line", LinkCheckRequired: false);

        if (strictness == VersionStrictness.Family)
        {
            if (candidate.PlatformVersion is null)
                return new AdoptionDecision(AdoptionVerdict.Decline,
                    $"sealed for framework identity {candidate.FrameworkIdentity}, which no "
                    + "_releases marker names — its platform line cannot be established, and "
                    + $"{nameof(VersionStrictness.Family)} adopts only within a known line",
                    LinkCheckRequired: false);
            if (live.PlatformVersion is null)
                return new AdoptionDecision(AdoptionVerdict.Decline,
                    "the running platform's version could not be determined, so its line cannot be "
                    + "compared", LinkCheckRequired: false);
            if (!SameFamily(candidate.PlatformVersion, live.PlatformVersion))
                return new AdoptionDecision(AdoptionVerdict.Decline,
                    $"sealed for platform {candidate.PlatformVersion} (line {MajorOf(candidate.PlatformVersion)}), "
                    + $"this is {live.PlatformVersion} (line {MajorOf(live.PlatformVersion)}) — another line",
                    LinkCheckRequired: false);
        }

        if (FloorDecline(candidate.MinMeshVersion, live.PlatformVersion) is { } floor)
            return new AdoptionDecision(AdoptionVerdict.Decline, floor, LinkCheckRequired: false);

        var basis = strictness == VersionStrictness.Family
            ? $"same platform line as {live.PlatformVersion}"
            : $"{nameof(VersionStrictness.Minimum)} strictness";
        return new AdoptionDecision(AdoptionVerdict.Adopt,
            $"sealed for {candidate.PlatformVersion ?? "an unnamed platform version"} "
            + $"(identity {candidate.FrameworkIdentity}) on {live.FrameworkIdentity}: {basis}"
            + (candidate.MinMeshVersion is null ? "" : $", floor {candidate.MinMeshVersion} satisfied")
            + " — the measured link check decides per assembly",
            LinkCheckRequired: true);
    }

    /// <summary>
    /// The link half: turns a per-assembly <see cref="ModuleLinkVerdict"/> into the final verdict
    /// for that assembly. A decision that required no link check passes through unchanged.
    /// </summary>
    public static AdoptionDecision AfterLink(AdoptionDecision decision, ModuleLinkVerdict link, Live live)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(live);
        if (!decision.LinkCheckRequired || decision.Verdict != AdoptionVerdict.Adopt)
            return decision;

        switch (link.State)
        {
            case ModuleLinkState.Linkable:
                return decision with
                {
                    Reason = decision.Reason + $"; {link.CheckedTypeReferences} platform type reference(s) resolve"
                             + (link.Advisories.IsDefaultOrEmpty
                                 ? ""
                                 : "; version skew that rolls forward, advisory: " + string.Join("; ", link.Advisories)),
                    LinkCheckRequired = false,
                };
            case ModuleLinkState.BindingConflict:
            {
                // The FileLoadException shape (#4083): a hard verdict like Unlinkable — the
                // assembly never binds on this platform, whatever its types look like.
                var conflicts = link.BindingConflicts.IsDefaultOrEmpty
                    ? "(none named)"
                    : string.Join(", ", link.BindingConflicts.Take(5))
                      + (link.BindingConflicts.Length > 5 ? $" … +{link.BindingConflicts.Length - 5}" : "");
                return live.RequirePrebuilt
                    ? new AdoptionDecision(AdoptionVerdict.Refuse,
                        $"{link.Module} references assembly versions this deployment does not carry "
                        + $"({conflicts}) — the platform's copy binds and never rolls back — and this "
                        + $"mesh does not compile module content "
                        + $"({PrebuiltAssemblySeeder.RequirePrebuiltConfigKey}): rebake the package for "
                        + $"framework {live.FrameworkIdentity}", LinkCheckRequired: false)
                    : new AdoptionDecision(AdoptionVerdict.CompileInstead,
                        $"{link.Module} references assembly versions this deployment does not carry "
                        + $"({conflicts}) — the platform's copy binds and never rolls back; the live "
                        + "source compiles instead", LinkCheckRequired: false);
            }
            case ModuleLinkState.Unlinkable:
            {
                var missing = link.MissingTypes.IsDefaultOrEmpty
                    ? "(none named)"
                    : string.Join(", ", link.MissingTypes.Take(5))
                      + (link.MissingTypes.Length > 5 ? $" … +{link.MissingTypes.Length - 5}" : "");
                return live.RequirePrebuilt
                    ? new AdoptionDecision(AdoptionVerdict.Refuse,
                        $"{link.Module} links against platform types this deployment does not carry "
                        + $"({missing}), and this mesh does not compile module content "
                        + $"({PrebuiltAssemblySeeder.RequirePrebuiltConfigKey}): rebake the package for "
                        + $"framework {live.FrameworkIdentity}", LinkCheckRequired: false)
                    : new AdoptionDecision(AdoptionVerdict.CompileInstead,
                        $"{link.Module} links against platform types this deployment does not carry "
                        + $"({missing}) — the live source compiles instead", LinkCheckRequired: false);
            }
            default:
                return live.RequirePrebuilt
                    ? new AdoptionDecision(AdoptionVerdict.Refuse,
                        $"{link.Module}: whether its platform links resolve could not be established "
                        + $"({link.Detail ?? "indeterminate"}), and this mesh does not compile module content",
                        LinkCheckRequired: false)
                    : new AdoptionDecision(AdoptionVerdict.CompileInstead,
                        $"{link.Module}: whether its platform links resolve could not be established "
                        + $"({link.Detail ?? "indeterminate"}) — the live source compiles instead",
                        LinkCheckRequired: false);
        }
    }

    /// <summary>
    /// The dependency record a tolerantly adopted build is stamped with: the bundle's keys, each
    /// re-pointed at the LIVE platform's id (the assembly it will actually bind to here), the
    /// toolchain entry at this process's toolchain. The record is what the build-currency clause
    /// (<c>HasUsableBuild</c>) later reads; stamping the producer's ids would make a build the
    /// link check just proved loadable read as dependency-stale on its first activation and be
    /// recompiled anyway. Keys with no live id keep the producer's value. Pure.
    /// </summary>
    public static ImmutableSortedDictionary<string, string>? LiveStampOf(
        IReadOnlyDictionary<string, string>? bundleDependencies,
        Func<string, string?>? liveDependencyIdOf,
        string? liveToolchainId)
    {
        if (bundleDependencies is null)
            return null;
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in bundleDependencies)
        {
            if (key == Compiler.CompiledDependencies.ToolchainKey && liveToolchainId is { Length: > 0 })
                builder[key] = liveToolchainId;
            else if (key.StartsWith('!'))
                builder[key] = value;
            else
                builder[key] = liveDependencyIdOf?.Invoke(key) ?? value;
        }
        return builder.ToImmutable();
    }

    private static string? FloorDecline(string? minMeshVersion, string? running)
    {
        if (string.IsNullOrWhiteSpace(minMeshVersion))
            return null;
        if (string.IsNullOrWhiteSpace(running))
            return $"the bundle declares minMeshVersion {minMeshVersion} but the running platform's "
                   + "version could not be determined";
        return NuGetVersionComparer.Instance.Compare(running, minMeshVersion) < 0
            ? $"the bundle declares platform ≥ {minMeshVersion} but this deployment runs {running}"
            : null;
    }
}
