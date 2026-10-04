using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.GitSync;

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
/// head loaded, the retained previous generation's when that one did, and NOTHING otherwise (an
/// image copy, a substituted load, an unloaded module) — absent means "not judged", never "met".</para>
/// </summary>
/// <param name="landing">The landing service whose activation list names each generation's version.</param>
public sealed class LoadedPackageModuleReader(ModuleLandingService landing) : ILoadedPackageModules
{
    /// <inheritdoc />
    public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() =>
        landing.GetActivation().Take(1)
            .Select(activation => Resolve(activation, ModuleActivationStatus.LoadedModuleGenerations()));

    /// <summary>
    /// The pure half: each enabled, package-stamped entry whose module loaded from its head or its
    /// retained previous generation, keyed by package id (the <c>&lt;source&gt;/&lt;package&gt;</c>
    /// path's second segment, case-insensitive).
    /// </summary>
    /// <param name="activation">The deployment's activation list.</param>
    /// <param name="loadedGenerations">Module simple name → the generation leaf it loaded from.</param>
    public static ImmutableDictionary<string, LoadedPackageModule> Resolve(
        ModuleActivationList activation, IReadOnlyDictionary<string, string> loadedGenerations)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(loadedGenerations);
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
        return builder.ToImmutable();
    }
}
