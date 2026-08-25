#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The activation record under CONCURRENT WRITERS (#2090, #2189) — the case the whole module lane
/// actually runs in, and the one the old shape could not survive.
///
/// <para><b>What production does.</b> Every portal replica mounts the same RWX <c>/data</c>, and a
/// republish after a release pushes 30+ modules at once, landing on whichever replica the load
/// balancer picked. The old record was ONE <c>modules/activation.json</c> that each landing read,
/// appended to, and renamed over. That has two failure modes and neither is fixable by retrying:
/// concurrent landings of DIFFERENT modules LOSE each other's entries (last writer wins the whole
/// list), and the rename contends for the file's SMB lease against every reader and writer of the
/// same path — the 409 <c>Access to the path '/data/modules/activation.json' is denied</c> of
/// #2090, and the <c>FileNotFoundException</c> a reader gets from opening into the replace window,
/// which booted pods with NO store modules at all (#2189).</para>
///
/// <para><b>What these pin.</b> Not "the race is now unlikely" — that would be untestable and
/// untrue. They pin the STRUCTURAL property that makes the race impossible: a landing writes only
/// its own module's file, so two writers of different modules never touch a shared path. Every
/// test below fails against the single-shared-file design.</para>
/// </summary>
public class ModuleActivationContentionTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-contention-" + Guid.NewGuid().ToString("N"));

    public ModuleActivationContentionTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private Task Land(ModuleLandingService service, string name) =>
        service.LandModule(name, [(name + ".dll", [1, 2, 3])]).FirstAsync().ToTask();

    /// <summary>Every file under the deployment root, by relative path and content hash.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    file => Path.GetRelativePath(root, file).Replace('\\', '/'),
                    file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
            : [];

    /// <summary>
    /// 🚨 THE REPRO. Landing one module must not touch any byte belonging to another — that
    /// property, and only that property, is what makes two replicas landing at once safe.
    ///
    /// <para>Against the old single-file record this FAILS at the first assertion: Beta's landing
    /// rewrites <c>modules/activation.json</c>, which is where Alpha's record lives. That one
    /// rewrite is both defects at once — the lost update (Beta's writer had to re-serialize
    /// Alpha's entry, so a stale read drops it) and the lease contention (both replicas rename
    /// over the same path).</para>
    /// </summary>
    [Fact]
    public async Task LandingOneModule_TouchesNoFileBelongingToAnother()
    {
        using var replicaA = new ModuleLandingService(baseDirectory: root);
        using var replicaB = new ModuleLandingService(baseDirectory: root);

        await Land(replicaA, "MeshWeaver.Alpha");
        var before = Snapshot();
        before.Keys.Should().Contain(key => key.Contains("MeshWeaver.Alpha"),
            "the arrangement is only meaningful if Alpha's record is on disk");

        await Land(replicaB, "MeshWeaver.Beta");
        var after = Snapshot();

        var written = after
            .Where(entry => !before.TryGetValue(entry.Key, out var hash) || hash != entry.Value)
            .Select(entry => entry.Key)
            .ToArray();

        written.Should().NotBeEmpty("the landing must have written something");
        written.Should().OnlyContain(path => path.Contains("MeshWeaver.Beta"),
            "a landing that rewrites a path holding ANOTHER module's record is exactly the shared "
            + "mutable cell two replicas lose each other's entries on, and contend for the SMB "
            + "lease of");
        before.Keys.Where(key => key.Contains("MeshWeaver.Alpha"))
            .Should().OnlyContain(path => after[path] == before[path],
                "Alpha's bytes are not Beta's landing's business");
    }

    /// <summary>
    /// 🚨 THE LOST UPDATE, in the exact sequence production runs it. Boot reads the record, does
    /// its work, then consumes the restart flag — and the OLD code consumed it by writing the
    /// whole list BACK from the snapshot it read at the start. Any module another replica landed
    /// in between was silently erased by a pod that was merely starting up. On a rolling restart
    /// that is several pods doing it at once, which is why #2189's pods came up with none of
    /// their store modules.
    ///
    /// <para>Fails against the old shape: the boot write is <c>Write(root, snapshot with
    /// { PendingRestart = false })</c> and Zulu is not in <c>snapshot</c>.</para>
    /// </summary>
    [Fact]
    public async Task ConsumingTheRestartFlag_CannotErase_AModuleLandedMeanwhile()
    {
        using var replica = new ModuleLandingService(baseDirectory: root);
        await Land(replica, "MeshWeaver.Yankee");

        // A booting pod reads the record it is about to apply.
        var bootSnapshot = ModuleActivationSidecar.Read(root);
        bootSnapshot.Entries.Should().ContainSingle();
        bootSnapshot.PendingRestart.Should().BeTrue();

        // Another replica lands a module while this one is still booting.
        await Land(replica, "MeshWeaver.Zulu");

        // The booting pod now consumes the flag — the ONE thing it may write.
        ModuleActivationSidecar.SetPendingRestart(root, false);

        var after = ModuleActivationSidecar.Read(root);
        after.PendingRestart.Should().BeFalse("this boot IS the restart the flag was waiting for");
        after.Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal)
            .Should().Equal(["MeshWeaver.Yankee", "MeshWeaver.Zulu"],
                "a pod starting up must never delete a module another replica just landed");
    }

    /// <summary>
    /// 🚨 The hazard the two tests above exist to exclude, demonstrated — so nobody "simplifies"
    /// the landing lane back onto the whole-list write.
    ///
    /// <para><see cref="ModuleActivationSidecar.Write"/> is the ADMINISTRATIVE form and rewrites
    /// every module's record from the caller's list. Called with a snapshot taken before someone
    /// else's landing, it erases that landing — silently, with no error and no retry that could
    /// help. That is precisely what the landing service and the boot path used to do on EVERY
    /// install and EVERY startup. The remedy is not a lock: it is that those two paths now write
    /// <see cref="ModuleActivationSidecar.WriteEntry"/> and
    /// <see cref="ModuleActivationSidecar.SetPendingRestart"/>, which touch one module and one
    /// marker.</para>
    /// </summary>
    [Fact]
    public async Task TheWholeListWrite_STILL_ClobbersAStaleSnapshot_WhichIsWhyTheLanesDoNotUseIt()
    {
        using var replica = new ModuleLandingService(baseDirectory: root);
        await Land(replica, "MeshWeaver.Yankee");
        var staleSnapshot = ModuleActivationSidecar.Read(root);

        await Land(replica, "MeshWeaver.Zulu");

        ModuleActivationSidecar.Write(root, staleSnapshot with { PendingRestart = false });

        ModuleActivationSidecar.Read(root).Entries.Select(entry => entry.Name)
            .Should().Equal(["MeshWeaver.Yankee"],
                "the bulk form means BULK — it is the shape that lost modules, and the reason no "
                + "runtime path may call it");
    }

    /// <summary>
    /// The same property under genuine concurrency: two replicas, each with its OWN in-process IO
    /// pool (the cap-1 pool bounds one process and cannot serialize across pods), landing a batch
    /// at the same time. Every entry survives. The assertion is deterministic even though the
    /// interleaving is not — which is the point of removing the shared cell rather than timing it.
    /// </summary>
    [Fact]
    public async Task TwoReplicasLandingAtOnce_LoseNothing()
    {
        using var replicaA = new ModuleLandingService(baseDirectory: root);
        using var replicaB = new ModuleLandingService(baseDirectory: root);

        var expected = Enumerable.Range(0, 12).Select(i => $"MeshWeaver.Mod{i:00}").ToArray();
        await Task.WhenAll(expected.Select((name, i) =>
            Land(i % 2 == 0 ? replicaA : replicaB, name)));

        ModuleActivationSidecar.Read(root).Entries.Select(entry => entry.Name)
            .Order(StringComparer.Ordinal).Should().Equal(expected);
    }

    /// <summary>
    /// 🚨 #2189's severity, pinned. One unreadable record must cost exactly that ONE module.
    ///
    /// <para>The old reader collapsed ANY failure — a transient SMB <c>ENOENT</c> from opening into
    /// another replica's rename, just as much as real corruption — into the empty list, and then
    /// logged that store modules "will NOT load". So one blip during boot demoted a pod to the
    /// appsettings baseline for its entire lifetime, silently, with every other module's record
    /// perfectly intact on disk.</para>
    /// </summary>
    [Fact]
    public async Task AnUnreadableRecord_CostsOnlyItsOwnModule_AndIsReportedLoudly()
    {
        using var replica = new ModuleLandingService(baseDirectory: root);
        await Land(replica, "MeshWeaver.Good");
        await Land(replica, "MeshWeaver.Bad");

        File.WriteAllText(ModuleActivationSidecar.EntryPath(root, "MeshWeaver.Bad"), "{ not json");

        var reported = new List<string>();
        var list = ModuleActivationSidecar.Read(root, reported.Add);

        list.Entries.Select(entry => entry.Name).Should().Equal(["MeshWeaver.Good"],
            "the readable records must survive their neighbour's corruption");
        reported.Should().ContainSingle().Which.Should().Contain("MeshWeaver.Bad",
            "the skip has to be loud AND name what was skipped — an unreadable record that reads "
            + "as 'nothing pending' is the absence of evidence dressed as evidence");
    }

    /// <summary>
    /// Deployments already on disk carry the legacy aggregate file, so it stays READABLE — and a
    /// per-module record WINS over it by name, because an uninstall written by the landing lane
    /// must beat a stale enabled row the frozen aggregate still holds.
    /// </summary>
    [Fact]
    public void TheLegacyAggregateIsStillRead_AndAPerModuleRecordOverridesIt()
    {
        Directory.CreateDirectory(Path.Combine(root, "modules"));
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(root), """
            {
              "entries": [
                { "name": "MeshWeaver.Legacy", "enabled": true },
                { "name": "MeshWeaver.Uninstalled", "enabled": true }
              ],
              "pendingRestart": false
            }
            """);
        ModuleActivationSidecar.WriteEntry(root,
            new ModuleActivationEntry { Name = "MeshWeaver.Uninstalled", Enabled = false });

        var list = ModuleActivationSidecar.Read(root);

        list.Entries.Should().HaveCount(2);
        list.Entries.Single(entry => entry.Name == "MeshWeaver.Legacy").Enabled.Should().BeTrue(
            "an existing deployment's records must not vanish on upgrade");
        list.Entries.Single(entry => entry.Name == "MeshWeaver.Uninstalled").Enabled.Should().BeFalse(
            "the record the landing lane wrote is the current one");
    }

    /// <summary>
    /// 🚨 An IO failure is not a parse failure, and the read must survive BOTH the same way.
    ///
    /// <para>An SMB sharing violation or lease conflict surfaces as <c>IOException</c> /
    /// <c>UnauthorizedAccessException</c>, not as bad JSON — and boot calls
    /// <see cref="ModuleActivationSidecar.Read"/> UN-WRAPPED, so an escaping exception takes the
    /// portal down over a transient volume blip. A symlink LOOP reproduces exactly that class
    /// (<c>ELOOP</c> → <c>IOException</c>) deterministically and without depending on the uid the
    /// test happens to run as — a permission-based arrangement would silently do nothing as root,
    /// which CI containers often are. Both halves are asserted: the neighbour still loads, and the
    /// failure is reported by name.</para>
    /// </summary>
    [Fact]
    public async Task AnIOFailureOnOneRecord_IsReportedAndSkipped_NeverThrown()
    {
        using var replica = new ModuleLandingService(baseDirectory: root);
        await Land(replica, "MeshWeaver.Good");

        var looped = ModuleActivationSidecar.EntryPath(root, "MeshWeaver.Looped");
        var partner = ModuleActivationSidecar.EntryPath(root, "MeshWeaver.Partner");
        File.CreateSymbolicLink(looped, partner);
        File.CreateSymbolicLink(partner, looped);

        var reported = new List<string>();
        var list = ModuleActivationSidecar.Read(root, reported.Add);

        list.Entries.Select(entry => entry.Name).Should().Equal(["MeshWeaver.Good"],
            "one unreadable record costs its own module and nothing else");
        reported.Should().HaveCount(2, "each unreadable record reports itself, by name");
        reported.Should().OnlyContain(line => line.Contains("MeshWeaver.Looped")
            || line.Contains("MeshWeaver.Partner"));
    }

    /// <summary>The legacy aggregate gets the same treatment: an IO failure there is reported and
    /// skipped, never thrown, because boot reads it before anything can catch.</summary>
    [Fact]
    public void AnIOFailureOnTheLegacyAggregate_IsReportedAndSkipped_NeverThrown()
    {
        Directory.CreateDirectory(ModuleActivationSidecar.SidecarPath(root));
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry { Name = "MeshWeaver.Good" });

        var reported = new List<string>();
        var list = ModuleActivationSidecar.Read(root, reported.Add);

        list.Entries.Select(entry => entry.Name).Should().Equal(["MeshWeaver.Good"]);
        reported.Should().ContainSingle().Which.Should().Contain("activation.json");
    }

    /// <summary>
    /// 🚨 The module name BECOMES a path, so it is refused at the writer rather than trusted from
    /// whichever caller got there. A record file named after its module is a path-traversal surface
    /// the moment a name can carry a separator.
    /// </summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData("dir/child")]
    [InlineData("..")]
    [InlineData("  ")]
    public void AModuleNameThatIsNotAFileName_IsRefused(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry { Name = name }));
    }

    /// <summary>An absent record is the fresh-deployment state: empty, and SILENT. It must never
    /// be reported as unreadable — that is the noise that made #2189's real signal unreadable.</summary>
    [Fact]
    public void AnAbsentRecord_IsAQuietEmptyAnswer()
    {
        var reported = new List<string>();

        var list = ModuleActivationSidecar.Read(Path.Combine(root, "never-deployed"), reported.Add);

        list.Entries.Should().BeEmpty();
        list.PendingRestart.Should().BeFalse();
        reported.Should().BeEmpty();
    }
}
