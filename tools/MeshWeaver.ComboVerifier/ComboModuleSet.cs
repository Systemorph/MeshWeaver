using System.Collections.Immutable;
using System.Security.Cryptography;
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
        var versions = ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        var notDeclared = ImmutableSortedSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        // 🚨 ONE BUILD PER ASSEMBLY NAME ACROSS THE WHOLE COMPOSED SET (MeshWeaver#3732), the same
        // refusal node-repo-gate.yml makes over its composed bundles: a `MeshWeaver.*` sibling rides
        // every bundle that references it, the loader binds the name to whichever copy it sees
        // first, and two distinct entry modules carrying two builds of one sibling would make the
        // gate judge an arbitrary one. Individually sealed sources do not prove their union agrees.
        var siblingBuilds = new Dictionary<string, (string Digest, string Module)>(StringComparer.OrdinalIgnoreCase);
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
                foreach (var file in files.Where(f =>
                             f.FileName.StartsWith("MeshWeaver", StringComparison.OrdinalIgnoreCase)
                             && f.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    var digest = Convert.ToHexString(SHA256.HashData(file.Bytes));
                    if (siblingBuilds.TryGetValue(file.FileName, out var seen))
                    {
                        if (!string.Equals(seen.Digest, digest, StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                $"'{file.FileName}' reaches the composed module set as two different "
                                + $"builds — carried by module '{seen.Module}' ({seen.Digest[..16]}) and "
                                + $"by module '{name}' ({digest[..16]}). The loader keeps whichever it "
                                + "sees first, so the gate would judge an arbitrary build "
                                + "(MeshWeaver#3732).");
                    }
                    else
                        siblingBuilds[file.FileName] = (digest, name);
                }
                Land(name, bundle, bytes, manifest!.Module!, files, fullRoot);
                landed[name] = bundle;
                versions[name] = manifest!.Module!.Version ?? manifest.Version;
            }
        }

        // 🚨 Declared modules and NONE composed is a missing INSTRUMENT, not a partial set: an
        // existing but empty bundle directory (a fetch that wrote nothing) must not run the gate
        // with no modules — the exact shape of MeshWeaver#4413.
        if (wanted.Count > 0 && landed.Count == 0)
            throw new InvalidOperationException(
                $"the combo declares {wanted.Count} compiled module(s) and the fetched bundle "
                + "directories carry none of them — the sealed module set was not composed, so the "
                + "gate would run without the modules this instance lands.");

        return new ComboModuleComposition
        {
            Root = fullRoot,
            Modules = [.. landed.Keys],
            BundleVersions = versions.ToImmutable(),
            NotSealed = [.. wanted.Where(w => !landed.ContainsKey(w)).Order(StringComparer.OrdinalIgnoreCase)],
            NotDeclared = [.. notDeclared],
        };
    }

    private static void Land(
        string name, string bundle, byte[] bytes, BundleReader.ModuleRef module,
        IReadOnlyList<BundleReader.ModuleFile> files, string root)
    {
        // 🚨 EVERY name and path is validated BEFORE anything touches disk. The module name becomes
        // a directory and the file names become paths under it, and both come from a PRODUCER's
        // manifest: matching a combo declaration does not make a name safe, and a check that ran
        // per file would already have written the files listed before the bad one.
        // ModuleLandingService.LandCore holds the same order.
        if (!IsFlatName(name))
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares the module name '{name}', which is not a flat directory "
                + "name — it would place the module outside the composition root.");
        foreach (var file in files.Where(f => !IsFlatName(f.FileName)))
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares module file '{file.FileName}', which is not a flat file "
                + "name.");
        var entry = name + ".dll";
        if (!files.Any(f => string.Equals(f.FileName, entry, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares module '{name}' but does not carry its complete closure "
                + $"(no {entry}, or a declared file is missing) — a portal refuses to land it, so the "
                + "gate must not compose it either.");
        // Static assets and natives keep their module-relative path — the layout
        // ModuleLandingService writes and ModuleNativeAssets probes (MeshWeaver#4126).
        // 🚨 Both readers are all-or-nothing and answer EMPTY for a declared section whose entries
        // the archive does not carry — the reader's contract, not a refusal. Read alone, a bundle
        // with a complete DLL closure and a missing declared asset or native would be composed
        // with that section silently dropped: a module that loads and then 404s its assets or
        // throws DllNotFoundException, judged as though it were complete. So the DECLARED count is
        // the denominator and the read count the measurement, and a shortfall is refused.
        var assets = BundleReader.ReadModuleAssets(bytes);
        var natives = BundleReader.ReadModuleNativeAssets(bytes);
        var declaredAssets = module.StaticAssets?.Count ?? 0;
        var declaredNatives = module.NativeAssets?.Count ?? 0;
        if (assets.Count != declaredAssets || natives.Count != declaredNatives)
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares {declaredAssets} static asset(s) and {declaredNatives} "
                + $"native payload(s) for module '{name}' but carries {assets.Count} and "
                + $"{natives.Count} — an incomplete module is refused, never judged with a section "
                + "silently dropped.");
        var relative = assets.Concat(natives).ToList();
        foreach (var asset in relative.Where(a => !IsSafeRelativePath(a.RelativePath)))
            throw new InvalidOperationException(
                $"bundle '{bundle}' declares the module-relative path '{asset.RelativePath}', which "
                + "would escape the module folder.");

        var moduleDir = Path.Combine(root, "modules", name);
        Directory.CreateDirectory(moduleDir);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(moduleDir, file.FileName), file.Bytes);
        foreach (var asset in relative)
        {
            var target = Path.Combine(moduleDir, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, asset.Bytes);
        }
    }

    /// <summary>A single path segment: non-empty, no separator, not a self/parent reference, not
    /// rooted, and no character the file system refuses.</summary>
    internal static bool IsFlatName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not ("." or "..")
        && !value.Contains('/') && !value.Contains('\\')
        && !Path.IsPathRooted(value)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>A <c>/</c>-separated module-relative path whose every segment is a flat name.</summary>
    internal static bool IsSafeRelativePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\\') && !Path.IsPathRooted(value)
        && value.Split('/').All(IsFlatName);
}

/// <summary>The module half of a combo, as composed for one gate run.</summary>
public sealed record ComboModuleComposition
{
    /// <summary>The directory holding <c>modules/&lt;Name&gt;/</c> — mounted read-only into the
    /// gate.</summary>
    public string Root { get; init; } = "";

    /// <summary>The modules landed and handed to the tester, by entry-assembly name.</summary>
    public ImmutableArray<string> Modules { get; init; } = [];

    /// <summary>The version each landed module's bundle states (its module-level version, else
    /// the manifest's), by entry-assembly name; null where the producer stated none.</summary>
    public ImmutableSortedDictionary<string, string?> BundleVersions { get; init; } =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every landed module whose composed bundle is NOT the version the instance's install record
    /// names — one line each, <c>Name: installed X, composed Y</c>. The composed bytes are the
    /// sealed set for the CANDIDATE's identity, i.e. what an instance lands once it rolls there;
    /// where that differs from what it runs today, the verdict must say so rather than read as a
    /// verdict about the installed bytes.
    /// </summary>
    /// <param name="combo">The combo the composition was made for.</param>
    public ImmutableArray<string> VersionDivergence(InstanceCombo combo)
    {
        var installed = combo.Modules
            .Where(m => !string.IsNullOrWhiteSpace(m.Package?.CompiledModule))
            .GroupBy(m => m.Package!.CompiledModule!.Trim(), StringComparer.OrdinalIgnoreCase)
            // The RELEASED version is the SemVer a bundle states; `Version` can be a repository
            // commit sha on a record, so it is only the fallback for legacy records that carry no
            // released version.
            .ToImmutableDictionary(g => g.Key,
                g => string.IsNullOrWhiteSpace(g.First().Package!.ReleasedVersion)
                    ? g.First().Package!.Version
                    : g.First().Package!.ReleasedVersion,
                StringComparer.OrdinalIgnoreCase);
        return
        [
            .. Modules
                .Select(m => (Module: m,
                    Installed: installed.GetValueOrDefault(m),
                    Composed: BundleVersions.GetValueOrDefault(m)))
                .Where(x => !string.Equals(x.Installed, x.Composed, StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Module}: installed {x.Installed ?? "(unrecorded)"}, composed "
                             + (x.Composed ?? "(unstated by the bundle)")),
        ];
    }

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
