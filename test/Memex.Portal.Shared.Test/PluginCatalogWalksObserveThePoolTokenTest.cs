using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The plugin catalogue's pooled WALKS take the pool's token and stop on it (the #2480/#7049 shape,
/// found once <c>PooledLeafObservesItsTokenGuard</c> learned to see <c>InvokeBlocking</c>). Each
/// walk is handed an already-cancelled token, which is what a drain at teardown hands the next
/// step: it must throw <see cref="OperationCanceledException"/> — never fall back, never report the
/// volume unreadable, never record a landing — and the uncancelled control proves the same inputs
/// still read or land normally.
/// </summary>
public class PluginCatalogWalksObserveThePoolTokenTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.TokenWalks";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-catalog-token-walks-" + Guid.NewGuid().ToString("N"));

    public PluginCatalogWalksObserveThePoolTokenTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    private ModuleLandingOutcome Land(ModuleLandingService replica, CancellationToken ct) =>
        replica.LandCore(
            Module, [(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
            "test-build", "Plugins/TokenWalks", "1.0.0", minMeshVersion: null,
            [("wwwroot/a.txt", Encoding.UTF8.GetBytes("a"))], sourceCommit: null, nativeAssets: null,
            holdUnloadable: true, keepNewerHead: true, ct);

    private string[] StagingDirectories() =>
        Directory.Exists(Path.Combine(root, "modules"))
            ? Directory.GetDirectories(Path.Combine(root, "modules"), ".staging-*")
            : [];

    [Fact]
    public void ACancelledLanding_WritesNothing_AndLeavesNoStaging()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);

        Assert.ThrowsAny<OperationCanceledException>(() => Land(replica, Cancelled()));

        Assert.Empty(StagingDirectories());
        Assert.False(Directory.Exists(ModuleActivationSidecar.LandingRecordsDirectory(root, Module)),
            "a landing the pool cancelled before its commit rename records nothing");

        // The control: the same landing, uncancelled, lands and records.
        Land(replica, CancellationToken.None);
        Assert.NotEmpty(Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(root, Module), "*.json"));
    }

    private string Store => Path.Combine(root, "shared-store");

    private string[] StoreGenerations() =>
        Directory.Exists(Store)
            ? Directory.GetDirectories(Store).Where(d => !Path.GetFileName(d).StartsWith('.')).ToArray()
            : [];

    /// <summary>
    /// 🚨 Review on #7121: the SHARED-TIER half of a landing ran before any token check and, when it
    /// served the landing, the local half (where the checks were) was skipped — so an already-cancelled
    /// landing on the store's owner still PUBLISHED a sealed generation and wrote its landing record.
    /// </summary>
    [Fact]
    public void ACancelledLanding_OnTheStoresOwner_PublishesNothing_AndRecordsNothing()
    {
        var ownerRoot = Path.Combine(root, "owner");
        Directory.CreateDirectory(ownerRoot);
        using var owner = new ModuleLandingService(null, ownerRoot, beforeRecording: null)
            { SharedRoot = Store, PublishesToShared = true };

        Assert.ThrowsAny<OperationCanceledException>(() => Land(owner, Cancelled()));

        Assert.Empty(StoreGenerations());
        Assert.False(Directory.Exists(ModuleActivationSidecar.LandingRecordsDirectory(ownerRoot, Module)),
            "a landing the pool cancelled records nothing, whichever tier would have served it");

        // The control: uncancelled, the owner publishes to the store and records the landing.
        Land(owner, CancellationToken.None);
        Assert.Single(StoreGenerations());
        Assert.NotEmpty(Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(ownerRoot, Module), "*.json"));
    }

    /// <summary>The consumer's half of the same defect: a SEALED copy in the store served the landing
    /// and the cancelled landing recorded against it anyway.</summary>
    [Fact]
    public void ACancelledLanding_ServedByASealedStoreCopy_RecordsNothing()
    {
        var ownerRoot = Path.Combine(root, "owner");
        var consumerRoot = Path.Combine(root, "consumer");
        Directory.CreateDirectory(ownerRoot);
        Directory.CreateDirectory(consumerRoot);
        using (var owner = new ModuleLandingService(null, ownerRoot, beforeRecording: null)
                   { SharedRoot = Store, PublishesToShared = true })
            Land(owner, CancellationToken.None);
        using var consumer = new ModuleLandingService(null, consumerRoot, beforeRecording: null)
            { SharedRoot = Store, PublishesToShared = false };

        Assert.ThrowsAny<OperationCanceledException>(() => Land(consumer, Cancelled()));

        Assert.False(Directory.Exists(ModuleActivationSidecar.LandingRecordsDirectory(consumerRoot, Module)));

        Land(consumer, CancellationToken.None);
        Assert.NotEmpty(Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(consumerRoot, Module), "*.json"));
    }

    /// <summary>
    /// Review on #7121: the activation read checked the token only between MODULES, so a drain
    /// arriving inside one module's record log waited out every remaining record read. Here the drain
    /// arrives while the first record of the only module is being read, and the walk must stop before
    /// the next one.
    /// </summary>
    [Fact]
    public void ADrainInsideOneModulesRecordLog_StopsBeforeTheNextRecord()
    {
        using (var replica = new ModuleLandingService(null, root, beforeRecording: null))
            Land(replica, CancellationToken.None);
        var records = ModuleActivationSidecar.LandingRecordsDirectory(root, Module);
        // Sorts before the real record, so it is read first; reading it is where the drain lands.
        File.WriteAllText(Path.Combine(records, "0-unreadable.json"), "{ not json");
        using var cts = new CancellationTokenSource();
        var reports = new List<string>();

        Assert.ThrowsAny<OperationCanceledException>(() => ModuleActivationSidecar.ReadFor(
            root, message => { reports.Add(message); cts.Cancel(); }, () => null, cts.Token));

        Assert.Single(reports);
    }

    [Fact]
    public void TheActivationRecordWalk_StopsOnACancelledToken_AndReportsNothingCorrupt()
    {
        using (var replica = new ModuleLandingService(null, root, beforeRecording: null))
            Land(replica, CancellationToken.None);
        var corrupt = new List<string>();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ModuleActivationSidecar.ReadFor(root, corrupt.Add, () => null, Cancelled()));
        Assert.Empty(corrupt);

        var read = ModuleActivationSidecar.ReadFor(root, corrupt.Add, () => null, CancellationToken.None);
        Assert.Contains(read.Entries, e => e.Name == Module);
        Assert.Empty(corrupt);
    }

    [Fact]
    public void TheGenerationPin_StopsOnACancelledToken_AndDoesNotFallBackToTheSharedPath()
    {
        using (var replica = new ModuleLandingService(null, root, beforeRecording: null))
            Land(replica, CancellationToken.None);
        var entry = ModuleActivationSidecar.ReadFor(root, null, () => null, CancellationToken.None)
            .Entries.Single(e => e.Name == Module);
        var pins = Path.Combine(root, "pins");
        var warnings = new List<string>();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ModuleGenerationPin.PinnedLoadPath(root, entry, Cancelled(), pins, warnings.Add));
        Assert.Empty(warnings);

        var pinned = ModuleGenerationPin.PinnedLoadPath(root, entry, CancellationToken.None, pins, warnings.Add);
        Assert.StartsWith(pins, pinned);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheReleaseObservation_StopsOnACancelledToken_AndIsNeverCalledUnreadable()
    {
        // A root with a marker and an identity directory holding one source: the walk the
        // observation makes over publications.
        Directory.CreateDirectory(Path.Combine(root, "_releases"));
        File.WriteAllText(Path.Combine(root, "_releases", "4.0.0"), "s-ident\n");
        Directory.CreateDirectory(Path.Combine(root, "s-ident", "plugins"));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PublishedBundleCatalogue.ReadObservingToken(root, "4.0.0", Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PublishedBundleCatalogue.PublishedReleasesObservingToken(root, Cancelled()));

        Assert.Equal("s-ident",
            PublishedBundleCatalogue.ReadObservingToken(root, "4.0.0", CancellationToken.None).Target.FrameworkIdentity);
        Assert.Equal(new[] { "4.0.0" },
            PublishedBundleCatalogue.PublishedReleasesObservingToken(root, CancellationToken.None).Versions);
    }

    [Fact]
    public void ThePendingActivationRead_StopsOnACancelledToken()
    {
        var pending = new PendingModuleActivations(root);
        Assert.ThrowsAny<OperationCanceledException>(() => pending.Read(Cancelled()));
        Assert.False(pending.Read(CancellationToken.None).IsUndetermined);
    }
    /// <summary>
    /// Review on #7121: the pending-activation read forwarded its token to neither delegated walk, so
    /// a drain arriving inside the module-set records waited out every remaining record read. Two
    /// unreadable proposals; the drain lands while the FIRST is reported, and the walk must stop
    /// before opening the second — never reporting a cancel as one more unreadable record.
    /// </summary>
    [Fact]
    public void ADrainInsideTheModuleSetRecords_StopsBeforeTheNextRecord()
    {
        var sets = Path.Combine(root, "modules", "sets");
        Directory.CreateDirectory(sets);
        // Names this store never writes, so each is opened to be indexed by content.
        File.WriteAllText(Path.Combine(sets, "a.proposed.json"), "{ not json");
        File.WriteAllText(Path.Combine(sets, "b.proposed.json"), "{ not json");
        using var cts = new CancellationTokenSource();
        var reports = new List<string>();

        Assert.ThrowsAny<OperationCanceledException>(() => ModuleSetStore.ReadObservingToken(
            root, message => { reports.Add(message); cts.Cancel(); }, onNotice: null, cts.Token));

        Assert.Single(reports);

        // The control: uncancelled, both unreadable records are reported.
        var control = new List<string>();
        ModuleSetStore.ReadObservingToken(root, control.Add, onNotice: null, CancellationToken.None);
        Assert.Equal(2, control.Count);
    }

    /// <summary>Review on #7121: the plan-tier refusal markers were listed and read with no token.</summary>
    [Fact]
    public void TheTierRefusalWalk_StopsOnACancelledToken()
    {
        var entries = ModuleActivationSidecar.EntriesDirectory(root);
        Directory.CreateDirectory(entries);
        File.WriteAllText(Path.Combine(entries, Module + ModuleActivationSidecar.TierRefusedMarkerSuffix), "{ not json");

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ModuleActivationSidecar.ReadTierRefusals(root, Cancelled()));
        Assert.Empty(ModuleActivationSidecar.ReadTierRefusals(root, CancellationToken.None));
    }
}
