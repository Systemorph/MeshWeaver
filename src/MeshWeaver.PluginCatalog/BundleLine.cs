using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.Compiler;
using MeshWeaver.Hosting;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>THE one rule for which platform LINE a module bundle on a registry's index was built for,
/// and whether this process may take it</b> (policy <c>major-lines-run-separately</c>,
/// Doc/Architecture/MajorLinesRunSeparately → "The bundle index is read per line").
///
/// <para><b>The defect it closes (MeshWeaver.Plugins#5550).</b> A registry's bundle index lists
/// every module generation its shelf holds, newest version first, and the client took the FIRST
/// entry of a package. A registry on line 3 shelved the line-4 builds a line-4 producer published
/// (AI 4.0.1), so the head of AI became a line-4 build, and every line-3 consumer was offered 4.0.1
/// — refused at landing (it links MeshWeaver.Data 4.0.0.0 and CoreLib 11.0.0.0) — and never a newer
/// line-3 AI. The floor hold did not stop it: <see cref="MeshWeaver.Plugin.Packaging.PlatformFloor"/>
/// orders two published builds by RUN NUMBER alone, and the run counter is shared across lines, so a
/// <c>4.0.13296</c> floor read as satisfied on <c>3.1.13396</c>. The same pick let a line-4 process
/// take a line-3 build.</para>
///
/// <para><b>Module version numbers say nothing about the line</b> (line-3 AI ships as 1.52.0,
/// line-4 AI as 4.0.1), so the line is read from what an index entry states about its PLATFORM, in
/// this order, the first that names a major winning:</para>
/// <list type="number">
/// <item>the entry's own <see cref="PluginBundleClient.BundleRef.FrameworkMvid"/>, when it is a
/// platform compatibility key (<c>c&lt;major&gt;e&lt;epoch&gt;</c>) — a fact about the BYTES, recorded
/// by their producer;</item>
/// <item>the major of the entry's declared platform floor,
/// <see cref="PluginBundleClient.BundleRef.MinMeshVersion"/> (<c>minMeshVersion</c>, <c>4.0.13296</c> → 4);</item>
/// <item>the registry's own line — the index-level compatibility key — for an entry that states
/// neither: such an entry is the registry's own install record, or a generation shelved before
/// producers stated anything, and the registry is the evidence there is;</item>
/// <item>otherwise the LEGACY shared line (<see cref="LegacyLine"/>): every producer that states
/// nothing predates the split of lines.</item>
/// </list>
/// <para>Every major up to <see cref="LegacyLine"/> is ONE line, as it always was: a line-3 process
/// keeps taking a module whose floor names 2.x.</para>
///
/// <para><b>Only MODULE entries are judged.</b> An entry with no module is a NodeType bundle the
/// registry assembles from its own nodes; it states no floor or identity of its own on the wire, and
/// its line IS the index-level identity, which <c>PrebuiltAssemblySeeder.DeclineReason</c> already
/// compares (a foreign line declines the whole index). Filtering it here would only re-decide that.</para>
/// </summary>
public static class BundleLine
{
    /// <summary>The last platform major whose processes share one line (<see cref="ModuleRoot.LastSharedRootMajor"/>):
    /// every major at or below it is that line.</summary>
    public const int LegacyLine = ModuleRoot.LastSharedRootMajor;

    /// <summary>The line a platform major belongs to: <see cref="LegacyLine"/> for every major at or
    /// below it, the major itself after it. Pure.</summary>
    /// <param name="major">A platform major.</param>
    public static int LineOf(int major) => major <= LegacyLine ? LegacyLine : major;

    /// <summary>
    /// The line THIS process runs: the major of a test-registered
    /// <see cref="RunningPlatformVersionOverride"/> when one is registered (the catalog's floor test
    /// seam — production registers none), else <see cref="ModuleRoot.RunningMajor"/>, the core's own
    /// assembly major. Null only when neither names one, and then nothing is filtered.
    /// </summary>
    /// <param name="services">The calling hub's service provider.</param>
    public static int? RunningLine(IServiceProvider? services)
    {
        var major = services?.GetService(typeof(RunningPlatformVersionOverride)) is RunningPlatformVersionOverride pinned
            ? PrebuiltAdoptionPolicy.MajorOf(pinned.Version) ?? ModuleRoot.RunningMajor
            : ModuleRoot.RunningMajor;
        return major is { } m ? LineOf(m) : null;
    }

    /// <summary>
    /// Which line <paramref name="bundle"/> was built for, and the evidence that says so (see the
    /// type doc for the order). Pure.
    /// </summary>
    /// <param name="bundle">The index entry.</param>
    /// <param name="indexFrameworkMvid">The index-level identity — the registry's own line.</param>
    public static BundleLineReading Read(PluginBundleClient.BundleRef bundle, string? indexFrameworkMvid)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var own = bundle.FrameworkMvid?.Trim();
        if (PlatformCompatibility.TryParseKey(own, out var keyMajor, out _))
            return new(LineOf(keyMajor), $"its framework identity {own}", Stated: true);
        if (PrebuiltAdoptionPolicy.MajorOf(bundle.MinMeshVersion) is { } floorMajor)
            return new(LineOf(floorMajor), $"its declared platform floor {bundle.MinMeshVersion!.Trim()}", Stated: true);
        var registry = indexFrameworkMvid?.Trim();
        if (PlatformCompatibility.TryParseKey(registry, out var registryMajor, out _))
            return new(LineOf(registryMajor),
                $"the registry's own line {registry} (the entry states no identity and no floor)", Stated: false);
        return new(LegacyLine,
            "nothing — no identity, no floor, no registry line — so the legacy shared line "
            + LegacyLine.ToString(CultureInfo.InvariantCulture), Stated: false);
    }

    /// <summary>
    /// Splits <paramref name="index"/>'s entries into the ones this process's line may take and the
    /// module entries built for ANOTHER line, keeping the registry's order (newest first) in both.
    /// A null <paramref name="runningLine"/> keeps everything. Pure.
    /// </summary>
    /// <param name="index">The index as the registry served it.</param>
    /// <param name="runningLine">This process's line (<see cref="RunningLine"/>).</param>
    public static BundleLineSplit Split(PluginBundleClient.BundleIndex index, int? runningLine)
    {
        ArgumentNullException.ThrowIfNull(index);
        var all = index.Bundles ?? [];
        if (runningLine is not { } line)
            return new(all.ToImmutableArray(), [], 0);
        var kept = ImmutableArray.CreateBuilder<PluginBundleClient.BundleRef>();
        var foreign = ImmutableArray.CreateBuilder<ForeignLineBundle>();
        var inferred = 0;
        foreach (var bundle in all)
        {
            if (bundle is null)
                continue;
            if (string.IsNullOrWhiteSpace(bundle.Module))
            {
                kept.Add(bundle);
                continue;
            }
            var reading = Read(bundle, index.FrameworkMvid);
            if (reading.Line == line)
            {
                kept.Add(bundle);
                if (!reading.Stated)
                    inferred++;
            }
            else
                foreign.Add(new ForeignLineBundle(bundle.Plugin, bundle.Version, reading.Line, reading.Evidence));
        }
        return new(kept.ToImmutable(), foreign.ToImmutable(), inferred);
    }

    /// <summary>
    /// The sentence a package's MISS carries when the registry advertises it only for another line —
    /// or null when it advertises nothing for another line either. Pure.
    /// </summary>
    /// <param name="foreign">The index's withheld entries.</param>
    /// <param name="pluginId">The package.</param>
    /// <param name="runningLine">This process's line.</param>
    public static string? OnlyForeign(IReadOnlyList<ForeignLineBundle>? foreign, string pluginId, int? runningLine)
    {
        var mine = (foreign ?? []).Where(f => string.Equals(f.Plugin, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mine.Length == 0)
            return null;
        return $"the registry advertises {pluginId} only as build(s) for another platform line — "
               + string.Join(", ", mine.Select(f => f.Describe()))
               + $" — and this process runs line {runningLine?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)"}; "
               + "a build of another line is never landed here (policy major-lines-run-separately). "
               + "The module stays as it is until the registry serves a build of this line.";
    }
}

/// <summary>Which line an index entry was built for, and what says so.</summary>
/// <param name="Line">The line (<see cref="BundleLine.LineOf"/>).</param>
/// <param name="Evidence">The sentence naming the evidence.</param>
/// <param name="Stated">True when the ENTRY states it (identity or floor); false when it was
/// inferred from the registry or defaulted to the legacy line.</param>
public sealed record BundleLineReading(int Line, string Evidence, bool Stated);

/// <summary>One module entry withheld because it was built for another line.</summary>
/// <param name="Plugin">The package id.</param>
/// <param name="Version">The served version.</param>
/// <param name="Line">The line it was built for.</param>
/// <param name="Evidence">What says so.</param>
public sealed record ForeignLineBundle(string Plugin, string Version, int Line, string Evidence)
{
    /// <summary><c>AI 4.0.1 (line 4, by its declared platform floor 4.0.13296)</c>.</summary>
    public string Describe() =>
        $"{Plugin} {Version} (line {Line.ToString(CultureInfo.InvariantCulture)}, by {Evidence})";
}

/// <summary>An index split by line (<see cref="BundleLine.Split"/>).</summary>
/// <param name="Kept">The entries this process's line may take, in the registry's order.</param>
/// <param name="Foreign">The module entries built for another line.</param>
/// <param name="Inferred">How many kept module entries stated no line of their own.</param>
public sealed record BundleLineSplit(
    ImmutableArray<PluginBundleClient.BundleRef> Kept,
    ImmutableArray<ForeignLineBundle> Foreign,
    int Inferred);
