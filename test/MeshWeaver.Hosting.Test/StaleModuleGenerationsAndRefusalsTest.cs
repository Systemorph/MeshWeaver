using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>What a platform no longer runs is neither kept nor believed</b> (memex, 2026-11-23).
///
/// <para>(1) A generation the reader's platform MEASURED as not loading is not a fallback, so it
/// stops being referenced by the modules GC. (2) A refusal marker is keyed to the platform that
/// refused; one naming another build is ignored by readers and retired by the GC. Each case has a
/// negative control: a generation never measured, and a refusal of the running platform, stay.</para>
/// </summary>
public class StaleModuleGenerationsAndRefusalsTest : IDisposable
{
    private const string Module = "Acme.Widgets";
    private const string Live = "mvid-live";
    private const string Old = "mvid-old";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-stale-gen-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static ModuleLandingRecord Landing(string gen, string version, int minute) => new()
    {
        Name = Module,
        Directory = Module + "@" + gen,
        Version = version,
        RecordedAtUtc = new DateTime(2026, 11, 1, 0, minute, 0, DateTimeKind.Utc),
    };

    private static ModuleActivationSidecar.ModuleLog Log(
        ImmutableList<ModulePlatformVerdict> verdicts, params ModuleLandingRecord[] records) =>
        new([.. records], [], verdicts, []);

    private static ModulePlatformVerdict Verdict(string gen, string platform, bool linkable) => new()
    {
        Name = Module,
        Directory = Module + "@" + gen,
        Platform = platform,
        Linkable = linkable,
        RecordedAtUtc = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ModuleActivationEntry? Derive(ModuleActivationSidecar.ModuleLog log, string platform) =>
        ModuleActivationSidecar.DeriveEntry(Module, null, null, log, _ => true, log.LinkableOn(() => platform));

    [Fact]
    public void AGenerationMeasuredNotLoadingOnThisPlatform_IsNotTheFallback()
    {
        var log = Log([Verdict("0000000000000001", Live, linkable: false),
                       Verdict("0000000000000002", Live, linkable: true)],
            Landing("0000000000000001", "1.0.0", 1), Landing("0000000000000002", "2.0.0", 2));

        var entry = Derive(log, Live)!;

        Assert.Equal(Module + "@0000000000000002", entry.Directory);
        Assert.Null(entry.PreviousDirectory);
    }

    [Fact]
    public void TheSameGeneration_OnAPlatformWhereItLoads_StaysTheFallback()
    {
        var log = Log([Verdict("0000000000000001", Old, linkable: true),
                       Verdict("0000000000000002", Old, linkable: true),
                       Verdict("0000000000000001", Live, linkable: false),
                       Verdict("0000000000000002", Live, linkable: true)],
            Landing("0000000000000001", "1.0.0", 1), Landing("0000000000000002", "2.0.0", 2));

        Assert.Equal(Module + "@0000000000000001", Derive(log, Old)!.PreviousDirectory);
        Assert.Null(Derive(log, Live)!.PreviousDirectory);
    }

    [Fact]
    public void ANeverMeasuredGeneration_StaysTheFallback()
    {
        var log = Log([], Landing("0000000000000001", "1.0.0", 1), Landing("0000000000000002", "2.0.0", 2));

        Assert.Equal(Module + "@0000000000000001", Derive(log, Live)!.PreviousDirectory);
    }

    [Fact]
    public void WhileTheHeadItselfIsNotMeasuredLoading_AnUnloadableFallbackIsKept()
    {
        // A shelved landing built for a newer platform: the head does not load here either, and the
        // earlier generation is all that may be left to run. Nothing is dropped on a guess.
        var log = Log([Verdict("0000000000000001", Live, linkable: false),
                       Verdict("0000000000000002", Live, linkable: false)],
            Landing("0000000000000001", "1.0.0", 1), Landing("0000000000000002", "2.0.0", 2));

        Assert.Equal(Module + "@0000000000000001", Derive(log, Live)!.PreviousDirectory);
    }

    [Fact]
    public void ARefusalKeyedToAnotherPlatform_IsIgnoredAndRetired_ACurrentOneStays()
    {
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        ModuleActivationSidecar.SetRefused(root, "Stale.Module", new(
            "1.0", null, "held: references X 1.0.0.0, platform provides 3.1.0.0", at, "X 1.0.0.0", "3.1.0.0", Old));
        ModuleActivationSidecar.SetRefused(root, "Legacy.Module", new(
            "1.0", null, "held: legacy", at));
        ModuleActivationSidecar.SetRefused(root, "Current.Module", new(
            "1.0", null, "held: current", at, Platform: Live));

        var read = ModuleActivationSidecar.ReadCurrentRefusals(root, Live);
        Assert.Equal(["Current.Module"], read.Keys.ToArray());
        // The unfiltered read still sees all three: nothing is hidden by reading, only by retiring.
        Assert.Equal(3, ModuleActivationSidecar.ReadRefusals(root).Count);

        var removed = ModuleActivationSidecar.RetireStaleRefusals(root, Live, DateTimeOffset.UtcNow);

        Assert.Equal(2, removed);
        Assert.Equal(["Current.Module"], ModuleActivationSidecar.ReadRefusals(root).Keys.ToArray());
    }

    [Fact]
    public void AFreshStaleMarker_WithinTheGraceWindow_IsLeftAlone()
    {
        ModuleActivationSidecar.SetRefused(root, "Fresh.Module", new(
            "1.0", null, "held", DateTimeOffset.UtcNow, Platform: Old));

        Assert.Equal(0, ModuleActivationSidecar.RetireStaleRefusals(
            root, Live, DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Single(ModuleActivationSidecar.ReadRefusals(root));
    }

    [Fact]
    public void WhenTheLivePlatformIsUnknown_NothingIsJudged()
    {
        ModuleActivationSidecar.SetRefused(root, "Any.Module", new(
            "1.0", null, "held", DateTimeOffset.UtcNow.AddHours(-1), Platform: Old));

        Assert.Single(ModuleActivationSidecar.ReadCurrentRefusals(root, null));
        Assert.Equal(0, ModuleActivationSidecar.RetireStaleRefusals(root, null, DateTimeOffset.UtcNow));
    }
}
