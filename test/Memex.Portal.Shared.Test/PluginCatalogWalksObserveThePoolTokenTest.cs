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
}
