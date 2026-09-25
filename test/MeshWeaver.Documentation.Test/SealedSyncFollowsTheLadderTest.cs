#pragma warning disable CS1591
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The sealed-sync gate follows the LADDER</b> (policy <c>platform-backwards-compatibility</c>,
/// <c>Doc/Architecture/PublicationSealStarvation</c> → "Under the ladder").
///
/// <para>Publications are keyed on the platform COMPATIBILITY key (<c>c&lt;major&gt;e&lt;epoch&gt;</c>),
/// so every build of one epoch reads the same directory: a publication sealed by an OLDER build of
/// the key is this instance's — its sources advance to it with no seal for the running build
/// (platform 2 + plugin 1, then platform 2 + plugin 2). The one rung the ladder forbids is a
/// publication produced by a NEWER build than the running one (platform 1 + plugin 2): the gate
/// holds it and says so — both versions, and the remedy, the PLATFORM roll, which never waits for
/// this seal.</para>
///
/// <para>The shape is the control-instance deadlock measured on memex.systemorph.com on
/// 2026-09-25: the instance ran <c>3.0.0-ci.9218</c>, <c>Hosting/_GitSync</c> was Held at
/// <c>7545d355</c>, and <c>MeshWeaver.Plugins</c> <c>1470fbf3</c> was sealed only by
/// <c>3.0.0-ci.9321</c>. Under the per-build identity that hold named a newer IDENTITY and the
/// remedy was the same — roll the platform — but nothing distinguished it from a lane that had
/// stopped publishing. Under one key the reading has to carry the producing build, or the gate
/// would PROCEED onto bytes built for a platform the instance does not run.</para>
///
/// <para>Every case runs the REAL reader over a published root on disk (sentinel, markers and a
/// bundle whose manifest carries <c>producerPlatformVersion</c>), then the real gate. The negative
/// controls are the older/unknown-producer cases: a ladder rule that held everything would red them.</para>
/// </summary>
public class SealedSyncFollowsTheLadderTest
{
    private static readonly RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");
    private const string Key = "c003e001";
    private const string Head = "1470fbf3aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OldSync = "7545d355bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Running = "3.0.0-ci.9218";

    [Fact]
    public void APublicationProducedByANewerBuild_IsHeld_AndNamesThePlatformRoll()
    {
        using var root = new TempRoot();
        Publish(root.Path, Head, producer: "3.0.0-ci.9321");

        var (sources, outcome) = SealedPublicationIndex.ReadingForRunning(root.Path, Key, Running);

        outcome.Should().Be(SealedReadOutcome.Read);
        var plugins = sources.Should().ContainSingle().Which;
        plugins.HeldForNewerPlatform.Should().BeTrue();
        plugins.IsSealed.Should().BeFalse("sealed only for a NEWER platform is not sealed for this instance");
        plugins.ProducerPlatformVersion.Should().Be("3.0.0-ci.9321");

        var verdict = SealedSyncGate.Decide(Plugins, Head, OldSync, sources, Key);
        verdict.Proceed.Should().BeFalse("platform 1 + plugin 2 is the rung the ladder forbids");
        verdict.HoldReason.Should().Contain("3.0.0-ci.9321").And.Contain(Running)
            .And.Contain("PLATFORM roll").And.Contain("does not wait for this seal");

        var plan = SealedSyncGate.DecideBuild(Plugins, Head, OldSync, sources, Key, null);
        plan.Proceed.Should().BeFalse();
        plan.Notice.Should().Contain(n => n.MessageKey == "activity.gitsync.seal.heldNewerPlatform",
            "the Space's activity says WHY in the viewer's language, not 'not sealed'");
    }

    [Fact]
    public void APublicationProducedByAnOlderBuildOfTheSameKey_Proceeds_WithNoSealForTheRunningBuild()
    {
        using var root = new TempRoot();
        Publish(root.Path, Head, producer: "3.0.0-ci.9100");

        var (sources, _) = SealedPublicationIndex.ReadingForRunning(root.Path, Key, Running);

        sources.Should().ContainSingle().Which.IsSealed.Should().BeTrue();
        SealedSyncGate.Decide(Plugins, Head, OldSync, sources, Key).Proceed.Should().BeTrue(
            "a plugin built on an older build of the SAME key runs on this one — no re-seal for the running build");
    }

    [Fact]
    public void APublicationFromAProducerThatRecordsNoBuild_IsReadAsOlder_AndProceeds()
    {
        using var root = new TempRoot();
        Publish(root.Path, Head, producer: null);

        var (sources, _) = SealedPublicationIndex.ReadingForRunning(root.Path, Key, Running);

        sources.Should().ContainSingle().Which.IsSealed.Should().BeTrue();
        SealedSyncGate.Decide(Plugins, Head, OldSync, sources, Key).Proceed.Should().BeTrue();
    }

    [Fact]
    public void AnUnknownRunningBuild_HoldsNothingOnThisRule()
    {
        using var root = new TempRoot();
        Publish(root.Path, Head, producer: "3.0.0-ci.9321");

        SealedPublicationIndex.ReadingForRunning(root.Path, Key, runningPlatformVersion: null)
            .Sources.Should().ContainSingle().Which.IsSealed.Should().BeTrue(
                "'cannot compare' is not evidence of a newer producer — the rule only fires on a measured one");
    }

    [Fact]
    public void ApplyLadder_IsPure_AndOnlyTouchesASealedNewerPublication()
    {
        var sealedNewer = new SealedSource("plugins", "Systemorph/MeshWeaver.Plugins", Head, true, null)
        {
            ProducerPlatformVersion = "3.0.0-ci.9321",
        };
        SealedPublicationIndex.ApplyLadder(sealedNewer, Running).HeldForNewerPlatform.Should().BeTrue();
        SealedPublicationIndex.ApplyLadder(sealedNewer, "3.0.0-ci.9321").IsSealed.Should().BeTrue("the producing build itself");
        SealedPublicationIndex.ApplyLadder(sealedNewer, "3.0.0-ci.9400").IsSealed.Should().BeTrue("a later build of the key");

        var torn = sealedNewer with { IsSealed = false, Refusal = "no completion sentinel" };
        SealedPublicationIndex.ApplyLadder(torn, Running).Should().Be(torn, "an unsealed reading keeps its own refusal");
    }

    // ───────────────────────────────────────────── helpers

    /// <summary>Writes a flat sealed publication of 'plugins' under <see cref="Key"/>: markers,
    /// one bundle whose manifest records <paramref name="producer"/>, then the sentinel.</summary>
    private static void Publish(string root, string commit, string? producer)
    {
        var source = Path.Combine(root, Key, "plugins");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, SealedPublicationIndex.RepositoryMarkerFileName), "Systemorph/MeshWeaver.Plugins\n");
        File.WriteAllText(Path.Combine(source, SealedPublicationIndex.SourceCommitMarkerFileName), commit + "\n");
        using (var zip = ZipFile.Open(Path.Combine(source, "Store.zip"), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(NuGetPackageWriter.ManifestEntry);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(producer is null
                ? $$"""{"plugin":"Store","frameworkMvid":"{{Key}}","assemblies":[]}"""
                : $$"""{"plugin":"Store","frameworkMvid":"{{Key}}","assemblies":[],"producerPlatformVersion":"{{producer}}"}""");
        }
        File.WriteAllText(Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mw-ladderroot-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
