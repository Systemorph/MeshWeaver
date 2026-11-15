using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 One landed-module tree per platform major line on a shared volume (policy
/// <c>major-lines-run-separately</c>, Doc/Architecture/Modules → "One module tree per platform
/// line").
///
/// <para>The incident: a deployment rolling from major 3 to major 4 ran replicas of both on one
/// shared <c>/data</c>. The activation record names ONE head per module name, so the major-4 replica
/// booted the major-3 heads of store-only view packs — built against UI libraries major 4 no longer
/// ships — never became ready, and the major-3 replicas kept landing their own generations into the
/// same records. Each test below stands two majors up on ONE temp root through the real readers,
/// writers and sweep, never stubs.</para>
/// </summary>
public class ModuleTreePerPlatformLineTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-line-tree-{Guid.NewGuid():N}");

    public ModuleTreePerPlatformLineTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string V3 => ModuleRoot.ForLine(root, 3);
    private string V4 => ModuleRoot.ForLine(root, 4);

    [Fact]
    public void TheLegacyLineKeepsTheSharedRoot_LaterLinesGetTheirOwnTree()
    {
        ModuleRoot.ForLine(root, null).Should().Be(root, "an unknown major keeps the layout every image before the split used");
        ModuleRoot.ForLine(root, 2).Should().Be(root);
        ModuleRoot.ForLine(root, ModuleRoot.LastSharedRootMajor).Should().Be(root,
            "every image of the last shared-root major reads and writes the root itself and cannot be taught otherwise");
        ModuleRoot.ForLine(root, 4).Should().Be(Path.Combine(root, ModuleRoot.LinesDirectoryName, "4"));
        ModuleRoot.ForLine(root, 5).Should().Be(Path.Combine(root, ModuleRoot.LinesDirectoryName, "5"));
    }

    [Fact]
    public void AnUnconfiguredRootIsTheImagesOwnDirectory_WhateverTheMajor()
    {
        // Not shared with any other image, so nothing to separate — byte-for-byte ModuleRoot.Resolve.
        ModuleRoot.ResolveLine(configured: null, major: 4).Should().Be(AppContext.BaseDirectory);
        ModuleRoot.ResolveLine(configured: "  ", major: 4).Should().Be(AppContext.BaseDirectory);
        ModuleRoot.ResolveLine(new ConfigurationBuilder().Build()).Should().Be(ModuleRoot.Resolve((IConfiguration?)null));
    }

    [Fact]
    public void AConfiguredRootResolvesToTheRunningCoresLine()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ModuleRoot.ConfigKey] = $"  {root}  " })
            .Build();

        ModuleRoot.RunningMajor.Should().NotBeNull("the core's assembly version is pinned to <major>.<minor>.0.0 on every build");
        // The split routes on this one number, so a versioning drift in PluginCatalog alone would
        // silently send every replica back to the shared root. Pin it against another core assembly
        // stamped by the same Directory.Build.props expression ($(_VersionNumeric).0) — the message
        // hub every process loads — and pin that this tree (a line past the shared root) resolves
        // its own tree, so a drift below it fails here instead of reproducing the mixed-major roll.
        ModuleRoot.RunningMajor.Should().Be(typeof(MeshWeaver.Messaging.IMessageHub).Assembly.GetName().Version!.Major,
            "PluginCatalog must carry the same line major as the rest of core");
        (ModuleRoot.RunningMajor > ModuleRoot.LastSharedRootMajor).Should().BeTrue(
            "this source tree is a platform line past the shared root, so its replicas must resolve lines/<major>/modules/");
        ModuleRoot.ResolveLine(configuration).Should().Be(ModuleRoot.ForLine(root, ModuleRoot.RunningMajor));
        ModuleRoot.Resolve(configuration).Should().Be(root,
            "the instance-level root (setup manifest, boot progress) never moves with the line");
    }

    [Fact]
    public void TwoMajorsOnOneVolume_NeverSeeEachOthersHeads()
    {
        ModuleActivationSidecar.Write(V3, List(Entry("MeshWeaver.Blazor.Graph", "MeshWeaver.Blazor.Graph@v3gen", "3.1.13313")));
        ModuleActivationSidecar.Write(V4, List(Entry("MeshWeaver.Blazor.Graph", "MeshWeaver.Blazor.Graph@v4gen", "4.0.1")));

        Head(V3).Should().Be("MeshWeaver.Blazor.Graph@v3gen");
        Head(V4).Should().Be("MeshWeaver.Blazor.Graph@v4gen");

        // The old line keeps landing — exactly what its replicas do mid-roll under an Auto policy.
        ModuleActivationSidecar.Write(V3, List(Entry("MeshWeaver.Blazor.Graph", "MeshWeaver.Blazor.Graph@v3newer", "3.1.13400")));

        Head(V4).Should().Be("MeshWeaver.Blazor.Graph@v4gen", "an old-line landing can no longer flip the new line's head back");
        Head(V3).Should().Be("MeshWeaver.Blazor.Graph@v3newer");
    }

    [Fact]
    public void TheNewLineNeverAdoptsTheSharedRootsFlatOrLegacyEntries()
    {
        // Everything an image of the shared-root line leaves behind: per-module entry files, a
        // landing record, the legacy aggregate, the restart marker and a module set.
        ModuleActivationSidecar.Write(V3, List(
            Entry("MeshWeaver.Blazor.Chat", "MeshWeaver.Blazor.Chat@v3gen", "3.1.13313"),
            Entry("MeshWeaver.Markdown.Collaboration", "MeshWeaver.Markdown.Collaboration@v3gen", "3.1.13313")));
        Directory.CreateDirectory(Path.Combine(V3, "modules", "MeshWeaver.Blazor.Graph@v3gen"));
        ModuleActivationSidecar.WriteLanding(V3, new ModuleLandingRecord
        {
            Name = "MeshWeaver.Blazor.Graph",
            Directory = "MeshWeaver.Blazor.Graph@v3gen",
            Version = "3.1.13313",
            PackagePath = "Systemorph/Graph",
        }).Should().BeTrue();
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(V3),
            """{ "entries": [ { "name": "Legacy.Module", "enabled": true, "directory": "Legacy.Module@v3" } ] }""");
        ModuleActivationSidecar.SetPendingRestart(V3, true);
        ModuleSetStore.Propose(V3, List(Entry("MeshWeaver.Blazor.Chat", "MeshWeaver.Blazor.Chat@v3gen", "3.1.13313")), "v3-replica")
            .Should().NotBeNull();
        ModuleActivationSidecar.Read(V3).Entries.Should().HaveCount(4, "the shared-root line still sees all of it");

        var v4 = ModuleActivationSidecar.Read(V4);

        v4.Entries.Should().BeEmpty("a new-line replica lands its own generations; it never boots an old line's");
        v4.PendingRestart.Should().BeFalse("the old line's restart marker is the old line's");
        ModuleSetStore.Read(V4).Proposed.Should().BeNull("the module set an old-line wave proposed never steers the new line");
    }

    [Fact]
    public void EachLinesSweep_KeepsTheOtherLinesGenerations()
    {
        // Each line has a referenced head and an unreferenced orphan of its own.
        ModuleActivationSidecar.Write(V3, List(Entry("A", "A@v3head", "3.1.1")));
        ModuleActivationSidecar.Write(V4, List(Entry("A", "A@v4head", "4.0.1")));
        foreach (var dir in new[]
                 {
                     Path.Combine(V3, "modules", "A@v3head"), Path.Combine(V3, "modules", "A@v3orphan"),
                     Path.Combine(V4, "modules", "A@v4head"), Path.Combine(V4, "modules", "A@v4orphan"),
                 })
            Directory.CreateDirectory(dir);
        var later = DateTime.UtcNow.AddHours(1);

        // The shared-root line's sweep is the one every image of that line already runs: it walks
        // <root>/modules only, which lines/ is not under.
        ModuleLandingService.CollectGarbage(V3, logger: null, minAge: TimeSpan.Zero, nowUtc: later).Should().Be(1);
        ModuleLandingService.CollectGarbage(V4, logger: null, minAge: TimeSpan.Zero, nowUtc: later).Should().Be(1);

        Directory.Exists(Path.Combine(V3, "modules", "A@v3head")).Should().BeTrue();
        Directory.Exists(Path.Combine(V4, "modules", "A@v4head")).Should().BeTrue(
            "the old line's sweep never reaches the new line's generations");
        Directory.Exists(Path.Combine(V3, "modules", "A@v3orphan")).Should().BeFalse("negative control: the sweep does run");
        Directory.Exists(Path.Combine(V4, "modules", "A@v4orphan")).Should().BeFalse("negative control: the sweep does run");
        Directory.Exists(Path.Combine(root, ModuleRoot.LinesDirectoryName)).Should().BeTrue();
    }

    private static string? Head(string lineRoot) =>
        ModuleActivationSidecar.Read(lineRoot).Entries.Single().Directory;

    private static ModuleActivationList List(params ModuleActivationEntry[] entries) => new() { Entries = [.. entries] };

    private static ModuleActivationEntry Entry(string name, string directory, string version) => new()
    {
        Name = name,
        Enabled = true,
        Directory = directory,
        Version = version,
        Source = ModuleActivationSources.Store,
        PackagePath = "Systemorph/" + name,
    };
}
