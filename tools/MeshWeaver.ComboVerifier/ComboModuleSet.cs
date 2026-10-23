using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.ComboVerifier;

/// <summary>
/// The MODULE half of an instance's combo, composed into the gate the way every node-repo lane
/// composes it: sealed module bundles, landed under <c>&lt;root&gt;/modules/&lt;Name&gt;/</c>, and
/// handed to <c>mw-plugin-test</c> as <c>--module</c> entries.
///
/// <para>🚨 <b>Why this exists (MeshWeaver#4413).</b> Since the portal image stopped shipping the
/// synced modules (MeshWeaver.Plugins <c>edac5dc27</c>: "NO BASELINE ENTRY FOR A SYNCED MODULE"),
/// every instance gets the AI engine, Chat, Collaboration and the rest by LANDING each installed
/// package's module bundle. The combo records that half — <see cref="PackageCoordinate.CompiledModule"/>
/// on every such package — but nothing read it: the assembler materialised the CONTENT, and the gate
/// ran the tester with no <c>--module</c>, whose image ships no module of its own
/// (<c>TesterModules.ImageShipped</c> is empty since #2276). So <c>Skill</c> and <c>Agent</c> were
/// never registered, <c>Store</c> — first in install order — failed "NodeType(s) not registered:
/// Skill", and every later package failed "NodeType 'Store/Plugin' is not registered": 54 of 54 red
/// on every slot, a CONTENT-shaped verdict with an INSTRUMENT cause.</para>
///
/// <para><b>Where the bytes come from.</b> The publication sealed for the candidate's framework
/// identity — the unit of consistency every gate composes from (<c>compose-sealed-modules.sh</c>),
/// and what an instance on that platform lands. The caller fetches it; this class only SELECTS what
/// the combo declares and lays it out. A bundle the combo does not declare is NOT loaded: a module
/// the instance does not carry would register types the instance lacks and pass content it would
/// refuse.</para>
///
/// <para><b>Read with the platform's own reader</b> (<see cref="BundleReader"/>) — the same
/// all-or-nothing closure, static-asset and native-payload rules a portal's landing applies, so a
/// bundle the instance could not land is refused here too rather than half-composed.</para>
/// </summary>
public static class ComboModuleSet
{
    /// <summary>The file patterns a sealed module bundle is published under.</summary>
    private static readonly ImmutableArray<string> BundlePatterns = ["*.module.nupkg", "*.nupkg", "*.zip"];

    /// <summary>
    /// The compiled modules the combo's installed packages declare, by entry-assembly name, in a
    /// stable order. A package without a compiled module contributes nothing.
    /// </summary>
    public static ImmutableArray<string> DeclaredBy(InstanceCombo combo) =>
    [
        .. combo.Modules
            .Select(m => m.Package?.CompiledModule?.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Lands every bundle under <paramref name="bundleDirectories"/> whose module the combo
    /// <paramref name="declared"/> into <c><paramref name="root"/>/modules/&lt;Name&gt;/</c>.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> — the instrument is broken, nothing is
    /// verified — when a directory is missing, when two bundles carry the same module, or when a
    /// bundle declares a module it does not completely carry.</para>
    /// </summary>
    /// <param name="declared">The compiled modules the combo declares (<see cref="DeclaredBy"/>).</param>
    /// <param name="bundleDirectories">Directories holding the sealed bundles (searched
    /// recursively).</param>
    /// <param name="root">Where the modules are laid out; recreated empty.</param>
    public static ComboModuleComposition Compose(
        IReadOnlyCollection<string> declared, IReadOnlyList<string> bundleDirectories, string root)
    {
        var wanted = declared.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var fullRoot = Path.GetFullPath(root);
        if (Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
        Directory.CreateDirectory(Path.Combine(fullRoot, "modules"));

        var landed = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var notDeclared = ImmutableSortedSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in bundleDirectories)
        {
            if (!Directory.Exists(directory))
                throw new InvalidOperationException(
                    $"the module bundle directory '{Path.GetFullPath(directory)}' does not exist — "
                    + "the sealed module set was not fetched, so the gate would run without the "
                    + "modules this instance lands.");
            foreach (var bundle in BundlePatterns
                         .SelectMany(p => Directory.EnumerateFiles(directory, p, SearchOption.AllDirectories))
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(bundle);
                var (manifest, files) = BundleReader.ReadModule(bytes);
                var name = manifest?.Module?.AssemblyName?.Trim();
                if (string.IsNullOrEmpty(name))
                    continue; // a content-only bundle carries no module
                if (!wanted.Contains(name))
                {
                    notDeclared.Add(name);
                    continue;
                }
                if (landed.TryGetValue(name, out var first))
                    throw new InvalidOperationException(
                        $"module '{name}' is carried by two bundles ('{first}' and '{bundle}') — the "
                        + "loader keeps whichever it sees first, so the gate would judge an "
                        + "arbitrary build (MeshWeaver#3732).");
                Land(name, bundle, bytes, files, fullRoot);
                landed[name] = bundle;
            }
        }

        return new ComboModuleComposition
        {
            Root = fullRoot,
            Modules = [.. landed.Keys],
            NotSealed = [.. wanted.Where(w => !landed.ContainsKey(w)).Order(StringComparer.OrdinalIgnoreCase)],
            NotDeclared = [.. notDeclared],
        };
    }

    private static void Land(
        string name, string bundle, byte[] bytes, IReadOnlyList<BundleReader.ModuleFile> files, string root)
    {
        var entry = name + ".dll";
        if (!files.Any(f => string.Equals(f.FileName, entry, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares module '{name}' but does not carry its complete closure "
                + $"(no {entry}, or a declared file is missing) — a portal refuses to land it, so the "
                + "gate must not compose it either.");
        var moduleDir = Path.Combine(root, "modules", name);
        Directory.CreateDirectory(moduleDir);
        foreach (var file in files)
        {
            if (file.FileName.Contains('/') || file.FileName.Contains('\\')
                || file.FileName is "." or ".." || Path.IsPathRooted(file.FileName))
                throw new InvalidOperationException(
                    $"bundle '{bundle}' declares module file '{file.FileName}', which is not a flat "
                    + "file name.");
            File.WriteAllBytes(Path.Combine(moduleDir, file.FileName), file.Bytes);
        }
        // Static assets and natives keep their module-relative path — the layout
        // ModuleLandingService writes and ModuleNativeAssets probes (MeshWeaver#4126).
        foreach (var asset in BundleReader.ReadModuleAssets(bytes)
                     .Concat(BundleReader.ReadModuleNativeAssets(bytes)))
            File.WriteAllBytes(EnsureUnder(moduleDir, asset.RelativePath, bundle), asset.Bytes);
    }

    private static string EnsureUnder(string moduleDir, string relative, string bundle)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative)
            || relative.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares the module-relative path '{relative}', which would "
                + "escape the module folder.");
        var target = Path.GetFullPath(Path.Combine(moduleDir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return target;
    }
}

/// <summary>The module half of a combo, as composed for one gate run.</summary>
public sealed record ComboModuleComposition
{
    /// <summary>The directory holding <c>modules/&lt;Name&gt;/</c> — mounted read-only into the
    /// gate.</summary>
    public string Root { get; init; } = "";

    /// <summary>The modules landed and handed to the tester, by entry-assembly name.</summary>
    public ImmutableArray<string> Modules { get; init; } = [];

    /// <summary>Modules the combo declares that no fetched seal carries. They are not loaded —
    /// exactly as in the producing repository's own gate, which composes only what its seal holds —
    /// and they are NAMED, so a content failure that needs one is attributable.</summary>
    public ImmutableArray<string> NotSealed { get; init; } = [];

    /// <summary>Sealed modules the combo does not declare — deliberately NOT loaded.</summary>
    public ImmutableArray<string> NotDeclared { get; init; } = [];

    /// <summary>The tester arguments, as seen from inside the container where <see cref="Root"/>
    /// is mounted at <paramref name="mount"/>.</summary>
    public ImmutableArray<string> TesterArgs(string mount) =>
    [
        .. Modules.SelectMany(m => new[] { "--module", $"{mount}/modules/{m}/{m}.dll" }),
    ];
}
