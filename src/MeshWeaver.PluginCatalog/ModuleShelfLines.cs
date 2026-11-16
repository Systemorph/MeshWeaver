using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>A plugin registry's shelf, one per platform LINE</b> (policy <c>major-lines-run-separately</c>,
/// Doc/Architecture/MajorLinesRunSeparately §4b, MeshWeaver.Plugins#5550).
///
/// <para><b>The defect it closes.</b> A registry kept ONE shelf: one activation record per module,
/// whose head is the highest version the shelf holds (#3996) plus one retained previous generation.
/// Module versions say nothing about the line — line-3 AI ships as 1.52.0, line-4 AI as 4.0.1 — so the
/// first line-4 publish onto a line-3 registry became the head of every package it touched, the
/// line-3 build survived only as the single fallback, and the NEXT line-4 publish (4.0.2) pushed the
/// last line-3 build off the shelf entirely. Every line-3 consumer of that registry then had nothing
/// of its own line left to take.</para>
///
/// <para><b>The rule.</b> Each line has its own landed-module tree, the same tree a process of that
/// line would land into on the same volume (<see cref="ModuleRoot.ForLine"/>): the registry's own line
/// is its own landing service, unchanged; every other line gets a <see cref="ModuleLandingService"/>
/// of its own, rooted at that line's tree. An upload is shelved into the tree of the line it STATES
/// (<see cref="BundleLine.StatedLine"/> — its framework identity, else its declared floor; an upload
/// stating neither is the registry's own line, as it always was), and the index and the bundle route
/// read the tree of the line the CALLER is on (<see cref="BundleLine.CallerLine"/>). So a publish of
/// one line never moves, displaces or garbage-collects another line's head.</para>
///
/// <para>The services of other lines are created on first use and owned here, so their lifetime is
/// the mesh's. Each line's head and fallback keep every rule the single shelf had: the highest version
/// of THAT line is its head.</para>
/// </summary>
public sealed class ModuleShelfLines : IDisposable
{
    private readonly ModuleLandingService own;
    private readonly string? configuredRoot;
    private readonly Func<string, ModuleLandingService> create;
    private readonly ConcurrentDictionary<int, Lazy<ModuleLandingService>> others = new();
    private bool disposed;

    /// <summary>Creates the shelves.</summary>
    /// <param name="own">This process's own landing service — the shelf of <paramref name="ownLine"/>.</param>
    /// <param name="configuredRoot">The configured module root (<see cref="ModuleRoot.ConfigKey"/>), or null.</param>
    /// <param name="ownLine">This process's line (<see cref="BundleLine.RunningLine"/>); null treats every
    /// line as its own, which is the single shelf exactly as before.</param>
    /// <param name="create">Creates the landing service of another line's tree, given that tree's
    /// directory. Null creates a plain <see cref="ModuleLandingService"/> reading on
    /// <paramref name="own"/>'s read pool.</param>
    /// <param name="loggerFactory">Diagnostics for the services this creates.</param>
    public ModuleShelfLines(
        ModuleLandingService own, string? configuredRoot, int? ownLine,
        Func<string, ModuleLandingService>? create = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(own);
        this.own = own;
        this.configuredRoot = string.IsNullOrWhiteSpace(configuredRoot) ? null : configuredRoot.Trim();
        OwnLine = ownLine;
        this.create = create ?? (tree => new ModuleLandingService(
            loggerFactory?.CreateLogger<ModuleLandingService>(), tree) { ReadPool = own.ReadPool });
    }

    /// <summary>The line this process's own landing service shelves for, or null when unknown.</summary>
    public int? OwnLine { get; }

    /// <summary>
    /// The shelf of <paramref name="line"/>: this process's own landing service for its own line (and
    /// for an unknown line or an unknown own line), another line's own service otherwise.
    /// </summary>
    /// <param name="line">The platform line (<see cref="BundleLine.LineOf"/>), or null.</param>
    public ModuleLandingService For(int? line)
    {
        if (line is not { } l || OwnLine is not { } mine || BundleLine.LineOf(l) == mine)
            return own;
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = BundleLine.LineOf(l);
        return others.GetOrAdd(normalized, n => new Lazy<ModuleLandingService>(
            () => create(TreeFor(configuredRoot, own.BaseDirectory, OwnLine, n)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>The shelf an upload belongs on: the line it states (<see cref="BundleLine.StatedLine"/>),
    /// else this process's own.</summary>
    /// <param name="frameworkMvid">The upload's framework identity.</param>
    /// <param name="minMeshVersion">The upload's declared platform floor.</param>
    public ModuleLandingService ForUpload(string? frameworkMvid, string? minMeshVersion) =>
        For(BundleLine.StatedLine(frameworkMvid, minMeshVersion));

    /// <summary>The line an upload is shelved for — the line it states, else this process's own.
    /// Pure apart from <see cref="OwnLine"/>.</summary>
    /// <param name="frameworkMvid">The upload's framework identity.</param>
    /// <param name="minMeshVersion">The upload's declared platform floor.</param>
    public int? LineOfUpload(string? frameworkMvid, string? minMeshVersion) =>
        BundleLine.StatedLine(frameworkMvid, minMeshVersion) ?? OwnLine;

    /// <summary>
    /// The directory whose <c>modules/</c> is <paramref name="line"/>'s shelf. Pure.
    /// <list type="bullet">
    /// <item>The own line: <paramref name="ownTree"/>, this process's own tree.</item>
    /// <item>With a configured (shared) root: <see cref="ModuleRoot.ForLine"/> — the tree a process of
    /// that line lands into on the same volume, so a replica of that line boots exactly what this
    /// registry shelved for it.</item>
    /// <item>Without one: <c>&lt;own tree&gt;/lines/&lt;line&gt;</c> — beside the own tree, never the own
    /// tree itself (an unconfigured root is the image directory for every major, and the shelves of two
    /// lines must never share a path).</item>
    /// </list>
    /// </summary>
    /// <param name="configuredRoot">The configured module root, or null.</param>
    /// <param name="ownTree">This process's own tree.</param>
    /// <param name="ownLine">This process's line, or null.</param>
    /// <param name="line">The line asked for.</param>
    public static string TreeFor(string? configuredRoot, string ownTree, int? ownLine, int line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownTree);
        if (ownLine is { } mine && BundleLine.LineOf(line) == mine)
            return ownTree;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var shared = ModuleRoot.ForLine(configuredRoot.Trim(), BundleLine.LineOf(line));
            if (!SamePath(shared, ownTree))
                return shared;
        }
        return Path.Combine(ownTree, ModuleRoot.LinesDirectoryName,
            BundleLine.LineOf(line).ToString(CultureInfo.InvariantCulture));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.Ordinal);

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var shelf in others.Values.Where(l => l.IsValueCreated))
            shelf.Value.Dispose();
    }
}
