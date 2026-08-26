using System.Collections.Immutable;
using System.IO;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a deployment can honestly say about ONE module it declared under <c>Modules:Required</c>.
/// </summary>
public enum RequiredModuleState
{
    /// <summary>Loaded in this process, or resolvable from what this deployment carries. Nothing
    /// to report.</summary>
    Present,

    /// <summary>Not here YET, and the lane that delivers it is the store — a restart, a landing or
    /// a platform update brings it. Visible and named, never a rollout blocker: holding the
    /// rollout cannot make the registry deliver it.</summary>
    ExpectedLater,

    /// <summary>The IMAGE claims to ship it (<c>Modules:Assemblies</c> names it) and it is not
    /// there. Nothing on this deployment can produce it, and the pods of the previous generation
    /// still have it — so the rollout must STALL.</summary>
    Absent,

    /// <summary>It is HERE and it loaded, and its registration threw against this platform build
    /// (#2234), so it contributes nothing. Never <see cref="Present"/>: the assembly did load, so a
    /// check asking only "is it loaded?" would call a replica missing the feature healthy. The two
    /// halves disagree and must move together — which is the one thing a rollout CAN fix, unlike
    /// <see cref="ExpectedLater"/>.</summary>
    Incompatible,
}

/// <summary>One required module's verdict, with the sentence an operator acts on.</summary>
/// <param name="Entry">The raw <c>Modules:Required</c> value (e.g. <c>MeshWeaver.Speech.dll</c>).</param>
/// <param name="Name">Its assembly simple name.</param>
/// <param name="State">The verdict.</param>
/// <param name="Reason">Why — naming the lane and the remedy, never just "missing".</param>
public sealed record RequiredModuleVerdict(
    string Entry, string Name, RequiredModuleState State, string Reason);

/// <summary>
/// The readiness contract for <c>Modules:Required</c> (#2089): which declared-required modules are
/// here, which are on their way, and which are a FAULT that must hold a rollout.
///
/// <para>🚨 <b>Why the old one-bit answer was wrong in both directions.</b>
/// <c>MeshBuilderModuleActivation.MissingRequired</c> asks a single question — does the file
/// resolve from the image? — and reports Unhealthy for every "no". That was right while every
/// module shipped in the image. Once modules started LEAVING for the registry it became a gate
/// asserting something it cannot know: a required module that is not in the image is either a
/// pack the build dropped (a real fault; the previous pods still have it, so stalling preserves
/// the feature) or a store-delivered module the lane has not landed yet (stalling delivers
/// nothing — the registry that must serve it is itself a portal downstream of this very
/// rollout). Reported identically, the second wedged both prod rollouts on 2026-08-23, and the
/// only remedy anyone had was blanking <c>Modules__Required__0..4</c> on the live deployment as
/// standing revert-debt — a "gate" whose one escape hatch is deleting the declaration is the
/// skip-trapdoor with its polarity flipped: it fails on no evidence instead of passing on it.</para>
///
/// <para><b>The missing evidence was already on disk: <c>Modules:Assemblies</c>.</b> That list is
/// the IMAGE's own claim about what it carries. A module named under BOTH keys is the image's
/// responsibility — absent means the build lost it, and that stays <see cref="RequiredModuleState.Absent"/>
/// → Unhealthy → the rollout stalls, which is the case the gate was built for (3.0.0-rc5). A
/// module named under <c>Required</c> but NOT under <c>Assemblies</c> is by construction
/// store-delivered: the deployment is saying "I need this feature", never "my image ships it". Its
/// absence is <see cref="RequiredModuleState.ExpectedLater"/> — reported, named, with the exact
/// sub-reason and remedy, and NOT Healthy — but it does not hold a rollout that cannot fix it.</para>
///
/// <para>🚨 <b>Degraded is not lenient.</b> Every ExpectedLater module is named on <c>/health</c>,
/// in the payload and in the boot log, with which of the four sub-states it is in (never installed
/// / landed-awaiting-restart / landing incomplete / held above the platform floor) and what to do.
/// An operator can always tell "required and nothing here can produce it" from "expected, and here
/// is precisely what it is waiting for". What is gone is only the one behaviour that never helped:
/// stalling a rollout on a lane the rollout is upstream of.</para>
///
/// <para>Pure and total — the caller supplies configuration, the loaded set, the activation record
/// and both gates — so the whole contract is testable with no filesystem and no host.</para>
/// </summary>
public static class RequiredModuleStatus
{
    /// <summary>
    /// Classifies every declared-required module.
    /// </summary>
    /// <param name="requiredEntries">The raw <c>Modules:Required</c> values.</param>
    /// <param name="baselineEntries">The raw <c>Modules:Assemblies</c> values — the IMAGE's claim
    /// about what it carries, and the evidence that separates a lost pack from a store-delivered
    /// one.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    /// <param name="resolvesFromDeployment">Whether the raw entry resolves to a file that exists —
    /// production passes <c>MeshBuilder.ResolveModulePath</c> + <c>File.Exists</c>, the same
    /// resolution the boot loader used, so a verdict can never disagree with the line that
    /// preceded it.</param>
    /// <param name="activation">The persisted activation record.</param>
    /// <param name="landedDllExists">Whether a store entry's landed DLL is on the volume —
    /// production passes <see cref="ModuleActivationBoot.LandedModuleDllExists"/>.</param>
    /// <param name="platformGate">The ONE platform floor gate
    /// (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>).</param>
    public static ImmutableList<RequiredModuleVerdict> Classify(
        IEnumerable<string?>? requiredEntries,
        IEnumerable<string?>? baselineEntries,
        IReadOnlySet<string> loadedAssemblyNames,
        Func<string, bool> resolvesFromDeployment,
        ModuleActivationList? activation,
        Func<ModuleActivationEntry, bool> landedDllExists,
        Func<string?, string?> platformGate)
        => Classify(requiredEntries, baselineEntries, loadedAssemblyNames, resolvesFromDeployment,
            activation, landedDllExists, platformGate, []);

    /// <summary>
    /// As the seven-argument overload, plus the modules that loaded but could not REGISTER against
    /// this build (<see cref="Mesh.IncompatibleModule"/>, #2234).
    ///
    /// <para>🚨 <b>An overload, deliberately, not an optional parameter on the existing method.</b>
    /// Adding an optional parameter is source-compatible and BINARY-breaking — the signature is
    /// replaced, so a caller compiled against the old one gets MissingMethodException at runtime.
    /// That is precisely the change that aborted every memex-cloud pod for ~90 minutes and the
    /// reason this overload exists at all; making the fix in the shape of the bug would be a poor
    /// joke. The seven-argument form stays, forwarding an empty set, so a host compiled against the
    /// previous platform keeps working.</para>
    /// </summary>
    /// <param name="incompatibleModules">Modules whose registration threw at boot, by simple name.</param>
    public static ImmutableList<RequiredModuleVerdict> Classify(
        IEnumerable<string?>? requiredEntries,
        IEnumerable<string?>? baselineEntries,
        IReadOnlySet<string> loadedAssemblyNames,
        Func<string, bool> resolvesFromDeployment,
        ModuleActivationList? activation,
        Func<ModuleActivationEntry, bool> landedDllExists,
        Func<string?, string?> platformGate,
        IReadOnlyCollection<Mesh.IncompatibleModule> incompatibleModules)
    {
        ArgumentNullException.ThrowIfNull(incompatibleModules);
        ArgumentNullException.ThrowIfNull(loadedAssemblyNames);
        ArgumentNullException.ThrowIfNull(resolvesFromDeployment);
        ArgumentNullException.ThrowIfNull(landedDllExists);
        ArgumentNullException.ThrowIfNull(platformGate);

        var imageShips = (baselineEntries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => Path.GetFileNameWithoutExtension(entry!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var landed = (activation?.Entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var verdicts = ImmutableList.CreateBuilder<RequiredModuleVerdict>();
        foreach (var entry in requiredEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var name = Path.GetFileNameWithoutExtension(entry!);

            // 🚨 BEFORE the loaded check, which an incompatible module would otherwise satisfy:
            // its assembly IS loaded, it simply registered nothing. Asked in the other order this
            // reports Present and the operator learns nothing until a user reports a missing feature.
            var broken = incompatibleModules.FirstOrDefault(
                module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase));
            if (broken is not null)
            {
                verdicts.Add(new RequiredModuleVerdict(
                    entry!, name, RequiredModuleState.Incompatible, broken.Report()));
                continue;
            }

            if (loadedAssemblyNames.Contains(name))
            {
                verdicts.Add(new RequiredModuleVerdict(
                    entry!, name, RequiredModuleState.Present, "loaded in this process"));
                continue;
            }

            if (resolvesFromDeployment(entry!))
            {
                verdicts.Add(new RequiredModuleVerdict(
                    entry!, name, RequiredModuleState.Present,
                    "present on this deployment; it loads at the next restart"));
                continue;
            }

            // 🚨 The one question the old gate never asked. The image's OWN list is what says
            // whether stalling the rollout can preserve anything: a pack the build was supposed to
            // carry is a fault the previous generation does not share, while a store-delivered
            // module is not something a held rollout can conjure.
            if (imageShips.Contains(name))
            {
                verdicts.Add(new RequiredModuleVerdict(
                    entry!, name, RequiredModuleState.Absent,
                    "this image lists it under Modules:Assemblies but does not ship it — the build "
                    + "lost the pack. The previous pods still have it, so this rollout must not "
                    + "complete. Fix the build, or delist it from Modules:Required."));
                continue;
            }

            verdicts.Add(new RequiredModuleVerdict(
                entry!, name, RequiredModuleState.ExpectedLater, StoreReason(name)));
        }

        return verdicts.ToImmutable();

        string StoreReason(string name)
        {
            if (!landed.TryGetValue(name, out var record) || !record.Enabled)
                return "store-delivered and NOT installed on this instance — install the package "
                    + "from the registry, or delist it from Modules:Required if this deployment "
                    + "does not want the feature. The image never shipped it, so no rollout can "
                    + "deliver it.";
            if (platformGate(record.MinMeshVersion) is { } held)
                return $"store-delivered, landed, and HELD above this platform: {held}. A platform "
                    + "update satisfies the floor and that boot loads it.";
            if (!landedDllExists(record))
                return "store-delivered and recorded as installed, but its landed assembly is "
                    + "ABSENT — the landing did not complete, and no restart will fix it. "
                    + "Re-install the package.";
            return "store-delivered and landed on the volume, not yet loaded in this process — "
                + "a restart activates it.";
        }
    }

    /// <summary>Those a rollout must stall on — the image's own lost packs.</summary>
    public static ImmutableList<RequiredModuleVerdict> Absent(
        IEnumerable<RequiredModuleVerdict> verdicts) =>
        [.. (verdicts ?? []).Where(v => v.State == RequiredModuleState.Absent)];

    /// <summary>Those the store lane still owes — reported and named, never a rollout blocker.</summary>
    public static ImmutableList<RequiredModuleVerdict> ExpectedLater(
        IEnumerable<RequiredModuleVerdict> verdicts) =>
        [.. (verdicts ?? []).Where(v => v.State == RequiredModuleState.ExpectedLater)];

    /// <summary>
    /// Those that ARE here and could not register against this build (#2234). A rollout stalls on
    /// these, for the same reason it stalls on <see cref="Absent"/> and NOT on
    /// <see cref="ExpectedLater"/>: the previous generation is serving the feature, and unlike a
    /// store-delivered module this deployment CAN fix it — by moving the module set and the image
    /// together instead of one alone.
    /// </summary>
    public static ImmutableList<RequiredModuleVerdict> Incompatible(
        IEnumerable<RequiredModuleVerdict> verdicts) =>
        [.. (verdicts ?? []).Where(v => v.State == RequiredModuleState.Incompatible)];

    /// <summary>One line per module, so the probe payload and the boot log read identically.</summary>
    public static string Describe(IEnumerable<RequiredModuleVerdict> verdicts) =>
        string.Join("; ", (verdicts ?? []).Select(v => $"{v.Name}: {v.Reason}"));
}
