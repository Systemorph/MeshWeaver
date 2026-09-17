using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The REQUIRED-modules probe's disk cost must not grow with what it enumerates, and must not
/// be paid again on a volume that has not changed</b> (MeshWeaver#4608 — the sibling #3664 missed).
///
/// <para>#3664 memoised <see cref="PendingModuleActivations.Read()"/> behind a fingerprint of three
/// directory timestamps because it was walking the module volume on every startup and readiness
/// probe. The REQUIRED-modules probe asks the same volume the same three questions — the activation
/// sidecar, one existence probe per landed DLL, and one per declared entry — and was left doing all
/// of them per call. Measured on memex.systemorph.com 2026-09-17 across all three replicas:
/// <b>6.5, 6.5, 6.7, 6.8, 7.1, 7.3, 7.3, 7.5, 7.7, 8.0, 8.4, 9.0, 9.9 and 10.1 seconds per
/// probe</b>, against a <c>startupProbe</c> that waits 5 s — so no replica rolled onto the
/// candidate image could record a single startup success, and each was killed at its three-hour
/// budget and started over.</para>
///
/// <para><b>Why these assertions rather than a stopwatch.</b> A timing assertion on a developer
/// machine measures the machine, not the rule: the local filesystem answers in microseconds where
/// the shared Azure Files volume answers in milliseconds, so the defect is invisible to a clock
/// here and obvious to one there. What is identical in both places is the NUMBER OF QUESTIONS
/// asked, so that is what is pinned — the snapshot is read once per change, and the two per-entry
/// predicates are one memo shared by every probe taken against it. Each memo is a
/// <see cref="System.Lazy{T}"/>, so at-most-once is a property of the type and not of the timing:
/// <c>ConcurrentDictionary.GetOrAdd</c> promises one stored VALUE, never one factory call.</para>
///
/// <para>A memo that always answered <c>false</c> would satisfy every cost assertion here while
/// being completely wrong, so both predicates also get a positive and a negative case — and one
/// test pins that they answer about DIFFERENT trees, which is what keeps the required-modules
/// classification able to tell "the image has it" from "it landed and the landing did not
/// finish".</para>
/// </summary>
public class ModuleProbeInputsCostTest : IDisposable
{
    private const string ModuleA = "MeshWeaver.Payments.Stripe";
    private const string ModuleB = "MeshWeaver.Social";
    private const string ModuleC = "MeshWeaver.Speech";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-probe-inputs-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService landing;

    public ModuleProbeInputsCostTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 🚨 The property: however many probes arrive against an unchanged volume, the sidecar is read
    /// ONCE — and both per-entry predicates are the SAME delegate, so the memo behind them is one
    /// memo. Reference identity is the whole assertion: two probes holding one memo ask the
    /// filesystem about a given entry at most once in total; two probes holding two memos ask once
    /// EACH, which is the defect, and no timing is needed to tell them apart.
    /// </summary>
    [Fact]
    public async Task AnUnchangedVolume_IsReadOnce_AndEveryProbeSharesOneMemo()
    {
        await LandWave(ModuleA, ModuleB, ModuleC);
        var pending = new PendingModuleActivations(root);

        var first = pending.ReadProbeInputs();
        var second = pending.ReadProbeInputs();
        var third = pending.ReadProbeInputs();

        Assert.Equal(1, pending.DiskReads);
        Assert.Same(first.ResolvesFromDeployment, second.ResolvesFromDeployment);
        Assert.Same(first.ResolvesFromDeployment, third.ResolvesFromDeployment);
        Assert.Same(first.LandedDllExists, second.LandedDllExists);
        Assert.Same(first.LandedDllExists, third.LandedDllExists);
    }

    /// <summary>
    /// 🚨 The NEGATIVE CONTROL, without which the assertion above would be satisfied by a probe
    /// that simply never looks again: a landing moves the fingerprint, so the next probe reads the
    /// volume, reports the new module, and carries a FRESH memo — the old answers are not carried
    /// past the change that invalidates them.
    /// </summary>
    [Fact]
    public async Task ALanding_IsSeen_AndRetiresTheMemoWithIt()
    {
        await LandWave(ModuleA);
        var pending = new PendingModuleActivations(root);

        var before = pending.ReadProbeInputs();
        Assert.Equal(1, pending.DiskReads);
        Assert.Single(EnabledNames(before));

        await LandWave(ModuleB, ModuleC);

        var after = pending.ReadProbeInputs();
        Assert.Equal(2, pending.DiskReads);
        Assert.Equal(3, EnabledNames(after).Count);
        Assert.Contains(ModuleC, EnabledNames(after), StringComparer.OrdinalIgnoreCase);
        Assert.NotSame(before.ResolvesFromDeployment, after.ResolvesFromDeployment);
        Assert.NotSame(before.LandedDllExists, after.LandedDllExists);
    }

    /// <summary>
    /// 🚨 <b>BOTH predicates answer, and they answer about the filesystem rather than about
    /// nothing.</b> Without this the cost assertions above would pass over a pair of memos that
    /// always returned <c>false</c> — cheap, stable, reference-identical and completely wrong.
    /// Each gets a positive AND a negative case, because only the pair rules that out.
    /// </summary>
    [Fact]
    public async Task BothPredicatesAnswerAboutTheFilesystem()
    {
        await LandWave(ModuleA);
        var pending = new PendingModuleActivations(root);

        var inputs = pending.ReadProbeInputs();
        var landed = inputs.Activation!.Entries.Single(m =>
            string.Equals(m.Name, ModuleA, StringComparison.OrdinalIgnoreCase));

        Assert.True(inputs.LandedDllExists(landed),
            "the landed DLL of a module this test just landed was not found on the volume — the "
            + "memo is answering about something other than the module root");
        Assert.False(
            inputs.LandedDllExists(new ModuleActivationEntry
            {
                Name = "MeshWeaver.NeverLanded",
                Directory = "does-not-exist",
            }),
            "a generation nothing ever landed was reported present");

        // The IMAGE-side question, which is a different tree from the landed one on purpose: this
        // assembly's own closure is what MeshBuilder.ResolveModulePath falls back to, so a file
        // sitting beside the test host is the positive case an always-false memo cannot fake.
        var shipped = Path.GetFileName(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
        Assert.True(inputs.ResolvesFromDeployment(shipped),
            $"'{shipped}' is in this host's own directory and the resolver did not find it — the "
            + "image-side predicate is answering about nothing, which would make every declared "
            + "module read as absent while costing nothing to compute");
        Assert.False(inputs.ResolvesFromDeployment("MeshWeaver.NeverShipped.dll"),
            "a module no image ever carried was reported as resolving from this deployment");
    }

    /// <summary>
    /// 🚨 The two predicates ask about DIFFERENT trees, and the classification depends on it: the
    /// image-side resolver must NOT answer for a module that merely landed on the volume, or
    /// <c>RequiredModuleStatus.Classify</c> would report it Present and swallow the store branches
    /// that tell an operator a landing did not complete. This is the assertion that would fail if
    /// the memo were ever "tidied up" to the root-aware overload.
    /// </summary>
    [Fact]
    public async Task TheImageSideResolver_DoesNotAnswerForAMerelyLandedModule()
    {
        await LandWave(ModuleA);
        var inputs = new PendingModuleActivations(root).ReadProbeInputs();
        var landed = inputs.Activation!.Entries.Single(m =>
            string.Equals(m.Name, ModuleA, StringComparison.OrdinalIgnoreCase));

        Assert.True(inputs.LandedDllExists(landed), "the landed DLL should be on the volume");
        Assert.False(inputs.ResolvesFromDeployment(ModuleA + ".dll"),
            "a module that only LANDED was reported as resolving from the image. Classify asks the "
            + "image question first and answers Present on a yes, so this would hide 'landed, not "
            + "yet loaded', 'its landed assembly is ABSENT' and the plan-tier refusal — every "
            + "reason that tells an operator what to do.");
    }

    /// <summary>
    /// A sidecar that cannot be read is never an EMPTY one. Every unreadable file is named
    /// separately, because a probe that folds them into one sentence loses the file name an
    /// operator has to go and look at.
    /// </summary>
    [Fact]
    public async Task AnUnreadableEntry_IsNamed_NotSwallowed()
    {
        await LandWave(ModuleA);
        var entries = Directory.GetFiles(
            ModuleActivationSidecar.EntriesDirectory(root), "*.json").Single();
        await File.WriteAllTextAsync(entries, "{ this is not json",
            TestContext.Current.CancellationToken);

        var inputs = new PendingModuleActivations(root).ReadProbeInputs();

        Assert.NotEmpty(inputs.Unreadable);
        Assert.Contains(inputs.Unreadable, reason =>
            reason.Contains(Path.GetFileName(entries), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> EnabledNames(ModuleProbeInputs inputs) =>
        (inputs.Activation?.Entries ?? []).Select(m => m.Name).ToList();

    private async Task Land(string name) =>
        await landing.LandModule(name, [(name + ".dll", RealAssemblyBytes)])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    private async Task LandWave(params string[] names)
    {
        foreach (var name in names)
            await Land(name);
        await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
    }
}
