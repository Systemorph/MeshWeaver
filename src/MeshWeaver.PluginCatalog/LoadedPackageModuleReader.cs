using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The module host's answer to <see cref="ILoadedPackageModules"/> (MeshWeaver#6067 follow-up):
/// package id → the module generation THIS PROCESS loaded for it, and that generation's version.
///
/// <para><b>Loaded, never landed.</b> A landing is restart-as-activation: the activation head can
/// name 1.21 while the process still runs the 1.20.4 generation it booted on, and judging a
/// dependency floor against the head would let an import compile sources against a build that is
/// not there — the very shape this exists to stop. So the reading pairs each landed entry with the
/// generation leaf the loaded assembly actually came from
/// (<see cref="ModuleActivationStatus.LoadedModuleGenerations()"/>): the head's version when the
/// head loaded, the retained previous generation's when that one did.</para>
///
/// <para>🚨 <b>The IMAGE's own copy is judged too, at the version its seed stamp states</b>
/// (<see cref="ImageModuleSeed"/>, Systemorph/MeshWeaver.Plugins#2715). It used to be left out —
/// "absent means not judged" — and that made the image copy the one case the floor could never
/// hold, although it is the commonest copy there is. Measured on the control instance on
/// 2026-10-05: the image (Plugins <c>29bfaefb</c>) ships AI stamped <c>1.21.1</c>; Hosting's tree
/// declared <c>AI@^1.22.0</c> and called <c>LanePolicy</c>, which only AI 1.22 carries; the reading
/// had no entry for AI, the import wrote Hosting's sources, and twenty Hosting NodeTypes —
/// <c>InstanceAction</c> among them — parked at <c>CS0246 LanePolicy</c>. The seed's version can
/// only UNDERSTATE the image's bytes (an unsettled <c>manifest.lock</c> keeps the number while the
/// sources move, never the reverse — see <see cref="ImageModuleSeed.DeclineReason"/>), so judging
/// against it can hold an import the image would have satisfied, never admit one it cannot. That
/// is the direction a gate errs in.</para>
///
/// <para>Still NOTHING for a substituted load, an ambiguous one, an unloaded module, or an image
/// copy without a stamp: absent means "not judged", never "met".</para>
/// </summary>
/// <param name="landing">The landing service whose activation list names each generation's version.</param>
public sealed class LoadedPackageModuleReader(ModuleLandingService landing) : ILoadedPackageModules
{
    /// <inheritdoc />
    public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() =>
        landing.GetActivation().Take(1)
            .Select(activation =>
            {
                var loaded = ModuleActivationStatus.LoadedModuleGenerations();
                return ResolveWithImageCopies(activation, loaded, ImageCopiesOf(loaded));
            });

    /// <summary>
    /// The pure half: each enabled, package-stamped entry whose module loaded from its head or its
    /// retained previous generation, keyed by package id (the <c>&lt;source&gt;/&lt;package&gt;</c>
    /// path's second segment, case-insensitive). Image copies are not judged here — see
    /// <see cref="ResolveWithImageCopies"/>.
    /// </summary>
    /// <param name="activation">The deployment's activation list.</param>
    /// <param name="loadedGenerations">Module simple name → the generation leaf it loaded from.</param>
    public static ImmutableDictionary<string, LoadedPackageModule> Resolve(
        ModuleActivationList activation, IReadOnlyDictionary<string, string> loadedGenerations) =>
        ResolveWithImageCopies(activation, loadedGenerations, ImmutableDictionary<string, ImageModuleCopy>.Empty);

    /// <summary>
    /// <see cref="Resolve"/>, plus every module that loaded from the IMAGE's own copy, read at the
    /// version that copy's <see cref="ImageModuleSeed"/> states and keyed by the package the seed
    /// names. A landed generation that loaded wins over the image reading for the same package (it
    /// is what runs); an image copy without a package or an ordered version is left out.
    ///
    /// <para>A distinct name rather than an overload of <see cref="Resolve"/>: dependent
    /// repositories cite <c>&lt;see cref="LoadedPackageModuleReader.Resolve"/&gt;</c>, and an added
    /// overload would turn each of those into CS0419 under <c>-warnaserror</c>.</para>
    /// </summary>
    /// <param name="activation">The deployment's activation list.</param>
    /// <param name="loadedGenerations">Module simple name → the generation leaf it loaded from.</param>
    /// <param name="imageCopies">Module simple name → where the image ships that module and what its
    /// seed stamp states (<see cref="ImageCopiesOf"/> in production).</param>
    public static ImmutableDictionary<string, LoadedPackageModule> ResolveWithImageCopies(
        ModuleActivationList activation,
        IReadOnlyDictionary<string, string> loadedGenerations,
        IReadOnlyDictionary<string, ImageModuleCopy> imageCopies)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(loadedGenerations);
        ArgumentNullException.ThrowIfNull(imageCopies);
        var builder = ImmutableDictionary.CreateBuilder<string, LoadedPackageModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in activation.Entries)
        {
            if (!entry.Enabled
                || entry.PackagePath?.Split('/') is not { Length: 2 } segments
                || string.IsNullOrWhiteSpace(segments[1])
                || !loadedGenerations.TryGetValue(entry.Name, out var leaf))
                continue;
            var version = string.Equals(leaf, entry.Directory, StringComparison.Ordinal)
                ? entry.Version
                : string.Equals(leaf, entry.PreviousDirectory, StringComparison.Ordinal)
                    ? entry.PreviousVersion
                    : null;
            if (!string.IsNullOrWhiteSpace(version))
                builder[segments[1]] = new LoadedPackageModule(entry.Name, version);
        }

        foreach (var (module, copy) in imageCopies)
        {
            if (!loadedGenerations.TryGetValue(module, out var leaf)
                || !string.Equals(leaf, copy.Leaf, StringComparison.Ordinal)
                || copy.Seed.Package is not { Length: > 0 } package
                || copy.Seed.Version is not { Length: > 0 } version
                || builder.ContainsKey(package))
                continue;
            builder[package] = new LoadedPackageModule(module, version);
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The image's own copy of every loaded module that has one with a seed stamp: where it sits
    /// (<see cref="MeshBuilder.ResolveModulePath(string, string?)"/> with no landed root — the
    /// image's <c>modules/&lt;Name&gt;/</c> or the app closure) and what the stamp says. A module
    /// whose image copy carries no stamp is left out, so it stays unjudged.
    /// </summary>
    /// <param name="loadedGenerations">Module simple name → the generation leaf it loaded from.</param>
    public static ImmutableDictionary<string, ImageModuleCopy> ImageCopiesOf(
        IReadOnlyDictionary<string, string> loadedGenerations)
    {
        ArgumentNullException.ThrowIfNull(loadedGenerations);
        var builder = ImmutableDictionary.CreateBuilder<string, ImageModuleCopy>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in loadedGenerations.Keys)
        {
            var directory = Path.GetDirectoryName(MeshBuilder.ResolveModulePath(module + ".dll", null));
            if (string.IsNullOrEmpty(directory)
                || ImageModuleSeed.Read(directory, module) is not { } seed)
                continue;
            builder[module] = new ImageModuleCopy(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), seed);
        }
        return builder.ToImmutable();
    }
}

/// <summary>
/// Where the image ships one module and what its seed stamp states — the input
/// <see cref="LoadedPackageModuleReader.ResolveWithImageCopies"/> judges an image-copy load by.
/// </summary>
/// <param name="Leaf">The leaf of the directory holding the image's copy — compared ordinally with
/// the leaf <see cref="ModuleActivationStatus.LoadedModuleGenerations()"/> records for the loaded
/// assembly.</param>
/// <param name="Seed">The image copy's <see cref="ImageModuleSeed"/>.</param>
public sealed record ImageModuleCopy(string Leaf, ImageModuleSeed Seed);
