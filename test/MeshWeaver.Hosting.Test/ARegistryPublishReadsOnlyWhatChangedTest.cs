using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A registry publish (<c>POST /api/plugins/bundles/{plugin}</c>: <see cref="ModuleLandingService.ShelveModule"/>
/// then <see cref="ModuleLandingService.ProposeModuleSet"/>) must not re-read the records of every
/// OTHER module on the volume.
///
/// <para>Both steps derive the activation of every module. Before <see cref="ModuleLogMemo"/> each did
/// it by opening every landing record and platform verdict of every module, so one publish of one
/// module cost about two opens per record file on the share (1,403 opens for 40 modules × 5
/// landings, measured under strace). On the registry's Azure Files volume that was ~23 s per publish,
/// growing with every landing until the next boot's GC, and a 42-bundle publication outgrew its
/// 20-minute job cap.</para>
///
/// <para><b>How the test can fail.</b> After the volume has been read once, every OTHER module's
/// records are overwritten with bytes that do not match their names, keeping each file's name, length
/// and write time, so what the directory lists is unchanged. A publish that re-opens them finds records
/// that do not match their own names, drops those modules as unreadable, and the proposal REFUSES
/// ("could not be read whole"). A publish that reads only what changed proposes a set naming every
/// module. The controls prove the memo is not a blindfold: a record another replica lands, and a
/// corrupt record whose listing DID change, are both seen by the next read.</para>
/// </summary>
public class ARegistryPublishReadsOnlyWhatChangedTest : IDisposable
{
    private const int Modules = 12;
    private const int LandingsEach = 3;
    private const string Platform = "plat-a";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-regpost-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService landing;

    public ARegistryPublishReadsOnlyWhatChangedTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(null, root, null, platformIdentity: Platform);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The defect's own shape: one publish after the volume was read once.</summary>
    [Fact]
    public async Task APublish_DoesNotReopen_TheRecordsOfModulesThatDidNotChange()
    {
        await Populate();
        var before = landing.LogMemo.VolumeReads;

        foreach (var other in Enumerable.Range(1, Modules - 1))
            ScrambleRecordsKeepingTheListing(Name(other));

        await Shelve(Name(0), "2.0.0", salt: 9999);
        var proposed = await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        // The publish moved Acme.M0's head, so a new set is proposed.
        Assert.NotNull(proposed);
        proposed.Generations.Keys.Should().HaveCount(Modules,
            "every module is still read whole: the scrambled records were never re-opened");
        (landing.LogMemo.VolumeReads - before).Should().Be(1,
            "the publish read one module's records off the volume, the one it landed");
    }

    /// <summary>Control: a record landed by another replica sharing the volume changes the listing,
    /// so the next read sees it.</summary>
    [Fact]
    public async Task ARecordAnotherReplicaLands_IsSeenByTheNextRead()
    {
        await Populate();
        (await Activation()).Entries.Single(e => e.Name == Name(3)).Version
            .Should().Be($"1.0.{LandingsEach - 1}", "the premise: the newest landing is the head");

        using (var other = new ModuleLandingService(null, root, null, platformIdentity: Platform))
            other.LandCore(Name(3), [(Name(3) + ".dll", Bytes)], Platform, "Plugins/" + Name(3), "3.0.0", null,
                [("wwwroot/salt.txt", Encoding.UTF8.GetBytes("other-replica"))], null, null,
                holdUnloadable: true, keepNewerHead: true, TestContext.Current.CancellationToken);

        (await Activation()).Entries.Single(e => e.Name == Name(3)).Version.Should().Be("3.0.0",
            "a record that arrived since the last read changes the listing, so it is read");
    }

    /// <summary>Negative control: a corrupt record whose listing changed is re-read and still refuses
    /// the proposal, exactly as before the memo.</summary>
    [Fact]
    public async Task ACorruptRecordWhoseListingChanged_StillRefusesTheProposal()
    {
        await Populate();
        File.WriteAllText(RecordFiles(Name(5)).First(), "{ not the record this name addresses }");

        var proposal = () => landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        await proposal.Should().ThrowAsync<InvalidOperationException>(
            "a record whose length or write time moved is re-read, and an unreadable record refuses the proposal");
    }

    /// <summary>
    /// The invariant the memo rests on: a record file is written ONCE. Every writer into a record
    /// directory (landing, uninstall, verdict, older-image snapshot) goes through <c>WriteOnce</c>, which
    /// skips an existing name and publishes by a no-replace rename. So writing a record whose name is
    /// already on the volume must leave the file's bytes untouched, even when they differ.
    /// </summary>
    [Fact]
    public void ARecordName_IsNeverRewritten_TheMemosInvariant()
    {
        var record = new ModuleLandingRecord
        {
            Name = Name(7), Source = ModuleActivationSources.Store, PackagePath = "Plugins/" + Name(7),
            Directory = Name(7) + "@0123456789abcdef", Version = "1.0.0", FrameworkMvid = Platform,
        };
        ModuleActivationSidecar.WriteLanding(root, record).Should().BeTrue("the premise: the first write publishes");
        var file = RecordFiles(Name(7)).Single();
        File.WriteAllText(file, "{ different bytes under the same name }");

        ModuleActivationSidecar.WriteLanding(root, record).Should().BeFalse("an existing name is never written again");
        File.ReadAllText(file).Should().Be("{ different bytes under the same name }",
            "the second write of a name must not replace what the name holds");
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    private static string Name(int i) => $"Acme.M{i}";

    private static byte[] Bytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    private async Task Populate()
    {
        for (var k = 0; k < LandingsEach; k++)
            for (var m = 0; m < Modules; m++)
                await Shelve(Name(m), $"1.0.{k}", salt: k);
        await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }

    private Task<ModuleActivationList> Activation() =>
        landing.GetActivation().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private Task<ModuleLandingOutcome> Shelve(string name, string version, int salt) =>
        landing.ShelveModule(name, [(name + ".dll", Bytes)], frameworkMvid: Platform,
                packagePath: "Plugins/" + name, version: version,
                staticAssets: [("wwwroot/salt.txt", Encoding.UTF8.GetBytes(salt.ToString()))])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private string[] RecordFiles(string module) =>
        Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(root, module));

    /// <summary>Overwrites every record file of <paramref name="module"/> with bytes that do not match
    /// its name, keeping the name, the length and the write time, so the listing does not move.</summary>
    private void ScrambleRecordsKeepingTheListing(string module)
    {
        var files = RecordFiles(module);
        files.Should().NotBeEmpty($"the premise: {module} has records on the volume");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var length = info.Length;
            var writtenAt = info.LastWriteTimeUtc;
            File.WriteAllBytes(file, Enumerable.Repeat((byte)'x', (int)length).ToArray());
            File.SetLastWriteTimeUtc(file, writtenAt);
        }
    }
}
