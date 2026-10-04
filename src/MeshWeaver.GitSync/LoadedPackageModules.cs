using System.Collections.Immutable;

namespace MeshWeaver.GitSync;

/// <summary>
/// One package's compiled module as THIS PROCESS has it loaded (MeshWeaver#6067 follow-up): the
/// module's entry-assembly name and the version of the generation that actually loaded.
/// </summary>
/// <param name="Module">The module's entry-assembly name (<c>MeshWeaver.AI</c>).</param>
/// <param name="Version">The version of the LOADED generation — never the newest landed one, which
/// does not run until a restart.</param>
public sealed record LoadedPackageModule(string Module, string Version);

/// <summary>
/// 🚨 What a GitSync import consults before it writes a package's sources: package id → the module
/// this process RUNS for it (MeshWeaver#6067 follow-up). An import of a package whose
/// <c>requires</c> floor the loaded dependency does not meet is DECLINED
/// (<see cref="ModuleSyncDecision.DeclineUnmetRequirements"/>), so its sources neither move nor
/// compile against a dependency build that lacks what they call.
///
/// <para>Implemented by the module host (<c>MeshWeaver.PluginCatalog</c>, which knows the landed
/// generations and the package each belongs to); a mesh that registers none has no landed modules
/// and the check abstains — exactly the behaviour before it existed.</para>
/// </summary>
public interface ILoadedPackageModules
{
    /// <summary>The current reading: package id (case-insensitive) → its loaded module. A package
    /// whose loaded version cannot be established is ABSENT, which the decision reads as "not
    /// judged", never as "met" or "unmet".</summary>
    IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read();
}
