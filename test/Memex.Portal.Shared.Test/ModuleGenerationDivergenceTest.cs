using System.Reflection;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A replica running a SUPERSEDED module generation must be reported as pending (#3395).</b>
///
/// <para><b>The observation.</b> #3395 recorded two NodeType compiles on <c>memex-cloud</c> that
/// resolved DIFFERENT module sets — <c>MeshWeaver.Payments.Stripe</c> present in one and absent in
/// the other — and asked why ONE PROCESS would resolve two. It does not. Measured 2026-09-06:
/// three pods of one Deployment on ONE image (<c>3.0.0-rc9.ci.7693</c>) booted 11:33:39, 11:41:04
/// and 12:51:21 around a module landing wave at 12:18–12:27, and
/// <c>/tmp/meshweaver-pinned-modules/*/</c> held <b>39 of 40 generations different</b> between the
/// first two pods and the third (<c>MeshWeaver.Payments.Stripe@8f251f57</c> vs
/// <c>…@458afe55</c>, the shared sidecar naming <c>…@458afe55</c>). The two compiles ran on two of
/// those pods — <c>Store/Order</c> at 12:53:21 on the 12:51 pod, <c>Store/Plugin</c> at 13:09:01 on
/// the 11:41 pod — and stamped two different module fingerprints into the ONE shared compile
/// record. Two processes, two module sets, one record.</para>
///
/// <para><b>The defect these pin.</b> Nothing could SEE that. The per-process restart-as-activation
/// signal — the seam <c>/health</c>'s <c>PendingModuleActivationHealthCheck</c> reads — compared
/// the activation record against the loaded assembly SIMPLE NAMES, so it answers the INSTALL case
/// (name absent) and is blind to the UPDATE case (name present, generation moved). Both stale pods
/// answered <c>/health</c> → <c>Healthy</c> while running a 90-minute-old module set. That is the
/// gate-that-cannot-fail shape: a promise ("a restart activates them") that never fires for the
/// change a deployment makes continuously.</para>
///
/// <para>Pure: no filesystem, no host, no mesh — the derivation takes the activation list and what
/// the process loaded, and both are supplied here.</para>
/// </summary>
public class ModuleGenerationDivergenceTest
{
    private const string Module = "MeshWeaver.Payments.Stripe";

    /// <summary>The generation the deployment's sidecar activates — read off memex-cloud's
    /// <c>/data/modules/activation.d/MeshWeaver.Payments.Stripe.json</c> on 2026-09-06.</summary>
    private const string Activated = "MeshWeaver.Payments.Stripe@458afe55";

    /// <summary>The generation the 11:33 and 11:41 pods had pinned and loaded at the same moment.</summary>
    private const string Superseded = "MeshWeaver.Payments.Stripe@8f251f57";

    private static readonly Func<string?, string?> FloorSatisfied = _ => null;

    private static readonly Func<ModuleActivationEntry, bool> BytesPresent = _ => true;

    private static readonly Func<ModuleActivationEntry, bool> BytesGone = _ => false;

    private static ModuleActivationList Activation(string? directory = Activated) => new()
    {
        Entries =
        [
            new ModuleActivationEntry
            {
                Name = Module,
                PackagePath = "Plugins/Stripe",
                Version = "1.0.0",
                Directory = directory,
            },
        ],
    };

    private static IReadOnlySet<string> Loaded(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> Generations(
        params (string Name, string Directory)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Directory, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 🚨 THE assertion. The module's NAME is loaded, so the name-only rule called this pod current;
    /// the generation it is running was superseded, so a restart genuinely changes what this process
    /// resolves — which is precisely what "pending" promises.
    /// </summary>
    [Fact]
    public void APodRunningASupersededGeneration_IsPending()
    {
        var pending = ModuleActivationStatus.NotYetLoaded(
            Activation(),
            Loaded(Module),
            Generations((Module, Superseded)),
            FloorSatisfied, BytesPresent);

        pending.Should().ContainSingle(
                "the deployment activates " + Activated + " and this process is running "
                + Superseded + " — a restart is what closes that gap")
            .Subject.Name.Should().Be(Module);
    }

    /// <summary>
    /// The other half, so the rule cannot pass by always answering "pending": the pod that DID boot
    /// after the landing is on the activated generation and has nothing to wait for.
    /// </summary>
    [Fact]
    public void APodRunningTheActivatedGeneration_IsNotPending()
    {
        ModuleActivationStatus.NotYetLoaded(
                Activation(),
                Loaded(Module),
                Generations((Module, Activated)),
                FloorSatisfied, BytesPresent)
            .Should().BeEmpty("this process loaded exactly the generation the sidecar activates");
    }

    /// <summary>
    /// The two pods of one deployment, from one activation record, must give DIFFERENT answers —
    /// the divergence #3395 measured has to be visible from inside a replica, because there is no
    /// other place it can be seen.
    /// </summary>
    [Fact]
    public void TwoReplicasOfOneDeployment_DisagreeAboutTheSameActivationRecord()
    {
        var record = Activation();

        var behind = ModuleActivationStatus.NotYetLoaded(
            record, Loaded(Module), Generations((Module, Superseded)), FloorSatisfied, BytesPresent);
        var current = ModuleActivationStatus.NotYetLoaded(
            record, Loaded(Module), Generations((Module, Activated)), FloorSatisfied, BytesPresent);

        behind.Should().NotBeEmpty();
        current.Should().BeEmpty();
        behind.Count.Should().NotBe(current.Count,
            "one record read by two processes with different pinned generations is exactly the "
            + "state that let two NodeType compiles stamp two module fingerprints into one node");
    }

    /// <summary>
    /// 🚨 Absence of evidence is not a mismatch. A process that cannot say WHERE it loaded a module
    /// from must not print a restart prompt: the promise would be unfalsifiable and nothing would
    /// clear it. Under-reporting costs a signal; over-reporting costs the signal's credibility.
    /// </summary>
    [Fact]
    public void AnUnknownLoadedGeneration_IsNotAMismatch()
    {
        ModuleActivationStatus.NotYetLoaded(
                Activation(),
                Loaded(Module),
                Generations(),
                FloorSatisfied, BytesPresent)
            .Should().BeEmpty("nothing here establishes that a different generation is loaded");
    }

    /// <summary>An entry with no recorded generation is the legacy fixed <c>modules/&lt;name&gt;/</c>
    /// folder: it names nothing to compare against, so the name-only rule still governs it.</summary>
    [Fact]
    public void AnEntryWithNoRecordedGeneration_IsNotAMismatch()
    {
        ModuleActivationStatus.NotYetLoaded(
                Activation(directory: null),
                Loaded(Module),
                Generations((Module, Superseded)),
                FloorSatisfied, BytesPresent)
            .Should().BeEmpty("a legacy entry activates a folder, not a generation");
    }

    /// <summary>
    /// The pending/unresolvable split survives the update case, and the buckets stay disjoint: when
    /// the ACTIVATED generation's bytes are not on the volume, no restart moves this process onto
    /// it, so the remedy is re-install and the entry must not read as "wait for a restart".
    /// </summary>
    [Fact]
    public void ASupersededPodWhoseActivatedBytesAreGone_IsUnresolvableNotPending()
    {
        var record = Activation();
        var generations = Generations((Module, Superseded));

        ModuleActivationStatus.NotYetLoaded(
                record, Loaded(Module), generations, FloorSatisfied, BytesGone)
            .Should().BeEmpty("no restart loads a generation directory that is not there");
        ModuleActivationStatus.Unresolvable(
                record, Loaded(Module), generations, FloorSatisfied, BytesGone)
            .Should().ContainSingle().Subject.Name.Should().Be(Module);
    }

    /// <summary>
    /// The pre-#3395 overloads keep their exact behaviour — they forward an EMPTY generation map,
    /// so a host compiled against the previous platform binds a method that still exists and still
    /// answers the install question. (The binary-compat rule
    /// <c>RequiredModuleStatus.Classify</c> already states, applied here.)
    /// </summary>
    [Fact]
    public void TheNameOnlyOverload_StillAnswersTheInstallQuestionUnchanged()
    {
        ModuleActivationStatus.NotYetLoaded(
                Activation(), Loaded(Module), FloorSatisfied, BytesPresent)
            .Should().BeEmpty("name-only cannot see a generation move — that is why it is not the "
                + "overload production calls");
        ModuleActivationStatus.NotYetLoaded(
                Activation(), Loaded(), FloorSatisfied, BytesPresent)
            .Should().ContainSingle("an absent name is still pending on the old overload");
    }

    /// <summary>
    /// The live-process reader takes the generation from the assembly's own directory leaf — which
    /// is what makes the comparison possible at all: <c>ModuleGenerationPin</c> copies a landed
    /// generation directory WITH its leaf into process-local storage, so a pinned module's location
    /// ends <c>…/&lt;name&gt;@&lt;id&gt;/&lt;name&gt;.dll</c> exactly as the shared one does.
    /// </summary>
    [Fact]
    public void LoadedModuleGenerations_ReadsTheDirectoryLeafOfEachLoadedAssembly()
    {
        var generations = ModuleActivationStatus.LoadedModuleGenerations(AppDomain.CurrentDomain);
        var self = typeof(ModuleGenerationDivergenceTest).Assembly;
        var expected = Path.GetFileName(Path.GetDirectoryName(self.Location))!;

        generations.TryGetValue(self.GetName().Name!, out var leaf).Should().BeTrue(
            "this assembly has an on-disk location, so its generation is knowable");
        leaf.Should().Be(expected,
            "the leaf of the containing directory IS the generation identity");
    }

    /// <summary>An assembly with no on-disk location says nothing about a generation — and silence
    /// is the correct answer, not a guess.</summary>
    [Fact]
    public void LoadedModuleGenerations_OmitsAnAssemblyWithNoLocation()
    {
        var generations = ModuleActivationStatus.LoadedModuleGenerations(AppDomain.CurrentDomain);

        generations.Keys.Should().OnlyContain(
            name => AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)
                    && HasLocation(a)),
            "a name in the map must be backed by an assembly that has a readable location");
    }

    private static bool HasLocation(Assembly assembly)
    {
        try
        {
            return !string.IsNullOrEmpty(assembly.Location);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
