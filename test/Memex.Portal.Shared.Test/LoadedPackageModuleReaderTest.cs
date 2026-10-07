using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#6067 follow-up: the dependency floor a GitSync import checks is judged against the
/// module generation this process LOADED, never against the newest LANDED one — a landing is
/// restart-as-activation, so the head can name 1.21.0 while 1.20.4 is what runs.
/// </summary>
public class LoadedPackageModuleReaderTest
{
    private static ModuleActivationEntry Ai(string? previousDirectory = "MeshWeaver.AI@1204", bool enabled = true) => new()
    {
        Name = "MeshWeaver.AI",
        PackagePath = "Plugins/AI",
        Directory = "MeshWeaver.AI@1210",
        Version = "1.21.0",
        PreviousDirectory = previousDirectory,
        PreviousVersion = "1.20.4",
        Enabled = enabled,
    };

    private static ModuleActivationList List(params ModuleActivationEntry[] entries) => new() { Entries = [.. entries] };

    [Fact]
    public void TheHeadLoaded_ReadsTheHeadsVersion()
        => LoadedPackageModuleReader.Resolve(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1210" })["AI"]
            .Version.Should().Be("1.21.0");

    /// <summary>THE case: 1.21.0 landed, the process still runs the 1.20.4 generation it booted on.</summary>
    [Fact]
    public void ALandingPendingRestart_ReadsTheVersionThatRuns()
        => LoadedPackageModuleReader.Resolve(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1204" })["ai"]
            .Version.Should().Be("1.20.4", "the floor is judged against what LOADED, and the key is case-insensitive");

    [Theory]
    [InlineData("MeshWeaver.AI")]      // the image's copy: no landing labelled it
    [InlineData("MeshWeaver.AI@9999")] // a generation neither the head nor the previous names
    public void AGenerationNoEntryLabels_IsNotJudged(string leaf)
        => LoadedPackageModuleReader.Resolve(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = leaf })
            .Should().BeEmpty("absent means not judged — never a guessed version");

    [Fact]
    public void AnUnloadedOrUninstalledModule_IsNotJudged()
    {
        LoadedPackageModuleReader.Resolve(List(Ai()), new Dictionary<string, string>()).Should().BeEmpty();
        LoadedPackageModuleReader.Resolve(List(Ai(enabled: false)),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1210" })
            .Should().BeEmpty();
    }

    // ───────────────────────── the IMAGE's copy is judged at its seed version (Plugins#2715)

    /// <summary>The image ships AI as <c>modules/MeshWeaver.AI/</c>, stamped as package AI 1.21.1 —
    /// the control instance's image (Plugins <c>29bfaefb</c>) on 2026-10-05.</summary>
    private static readonly IReadOnlyDictionary<string, ImageModuleCopy> ImageAi1211 =
        new Dictionary<string, ImageModuleCopy>
        {
            ["MeshWeaver.AI"] = new("MeshWeaver.AI", new ImageModuleSeed("MeshWeaver.AI", "AI", "1.21.1", "1af92ebc617c7648")),
        };

    /// <summary>🚨 The image copy loaded and its stamp names the package: the reading states it, at
    /// the stamped version — with or without a store entry for the same module.</summary>
    [Fact]
    public void TheImageCopyLoaded_ReadsTheSeedsVersion()
    {
        var loaded = new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI" };
        LoadedPackageModuleReader.ResolveWithImageCopies(List(Ai()), loaded, ImageAi1211)["AI"]
            .Should().Be(new LoadedPackageModule("MeshWeaver.AI", "1.21.1"),
                "the store head (1.21.0) did not load — the image's copy did, and its seed says what it is");
        LoadedPackageModuleReader.ResolveWithImageCopies(List(), loaded, ImageAi1211)["AI"].Version
            .Should().Be("1.21.1", "an image-only module, with no landing at all, is judged too");
    }

    /// <summary>A landed generation that LOADED is what runs, so it wins over the image reading.</summary>
    [Fact]
    public void ALandedGenerationThatLoaded_WinsOverTheImageReading()
        => LoadedPackageModuleReader.ResolveWithImageCopies(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1210" }, ImageAi1211)["AI"]
            .Version.Should().Be("1.21.0");

    /// <summary>No stamp, a stamp naming no package, or a load from somewhere other than the image's
    /// copy: still not judged.</summary>
    [Fact]
    public void AnImageCopyWithoutAStampOrAnotherLeaf_IsNotJudged()
    {
        var fromImage = new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI" };
        LoadedPackageModuleReader.ResolveWithImageCopies(List(Ai()), fromImage,
            new Dictionary<string, ImageModuleCopy>()).Should().BeEmpty();
        LoadedPackageModuleReader.ResolveWithImageCopies(List(Ai()), fromImage,
            new Dictionary<string, ImageModuleCopy>
            {
                ["MeshWeaver.AI"] = new("MeshWeaver.AI", new ImageModuleSeed("MeshWeaver.AI", null, "1.21.1", null)),
            }).Should().BeEmpty();
        LoadedPackageModuleReader.ResolveWithImageCopies(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@9999" }, ImageAi1211)
            .Should().BeEmpty("a generation neither a landing nor the image names is a substituted load");
    }

    /// <summary>
    /// 🚨 <b>THE incident, end to end through the real decision.</b> Hosting's tree declares
    /// <c>AI@^1.22.0</c>; the process runs the image's AI, stamped 1.21.1. The import must be
    /// DECLINED.
    /// <para><b>Negative control (in the test):</b> the reading the code had before this change
    /// (<see cref="LoadedPackageModuleReader.Resolve"/>, image copies not judged) lets the same tree
    /// SYNC — which is what wrote Hosting's sources on the control instance and parked twenty
    /// NodeTypes at <c>CS0246 LanePolicy</c>.</para>
    /// </summary>
    [Fact]
    public void ARequirementTheImagesCopyDoesNotMeet_DeclinesTheImport()
    {
        var loaded = new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI" };
        var hosting = new ModuleReading("Hosting", "Hosting", "07738e60f774423b", null)
        {
            Requires = ["Store@^1.0.0", "AI@^1.22.0", "Essentials@^1.11.0"],
        };
        ImmutableList<ModuleSyncOutcome> Judge(IReadOnlyDictionary<string, LoadedPackageModule> reading) =>
            ModuleSyncDecision.DeclineUnmetRequirements(
                ModuleSyncDecision.Decide([hosting],
                    new Dictionary<string, string> { ["Hosting"] = "0d6823302ed089d4" },
                    "3.0.0-ci.9984", reconcile: false),
                [hosting], reading);

        var declined = Judge(LoadedPackageModuleReader.ResolveWithImageCopies(List(Ai()), loaded, ImageAi1211)).Single();
        declined.Outcome.Should().Be(ModuleSyncOutcomeKind.Declined);
        declined.UnmetRequirement.Should().Be("AI@^1.22.0");
        declined.Reason.Should().Contain("1.21.1");

        Judge(LoadedPackageModuleReader.Resolve(List(Ai()), loaded)).Single().Outcome
            .Should().Be(ModuleSyncOutcomeKind.Synced,
                "negative control: without the image reading nothing judges the image copy, and the sources move");
    }

    /// <summary>
    /// The production reader of the image's copies, against a real stamp on disk in the image's own
    /// <c>modules/&lt;Name&gt;/</c> (this test host's base directory plays the image).
    /// </summary>
    [Fact]
    public void ImageCopiesOf_ReadsTheStampBesideTheImagesCopy()
    {
        var name = "MeshWeaver.Test.SeededModule" + Guid.NewGuid().ToString("N")[..8];
        var directory = Path.Combine(AppContext.BaseDirectory, "modules", name);
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, name + ".dll"), []);
            File.WriteAllText(Path.Combine(directory, ImageModuleSeed.FileName),
                $$"""{"schema":"{{ImageModuleSeed.Schema}}","module":"{{name}}","package":"Seeded","version":"2.3.4","moduleVersion":"abcdef0123456789"}""");

            var copies = LoadedPackageModuleReader.ImageCopiesOf(new Dictionary<string, string>
            {
                [name] = name,
                ["MeshWeaver.Test.NoSuchModule"] = "MeshWeaver.Test.NoSuchModule",
            });

            copies.Keys.Should().Equal([name]);
            var copy = copies[name];
            copy.Leaf.Should().Be(name);
            copy.Seed.Package.Should().Be("Seeded");
            copy.Seed.Version.Should().Be("2.3.4");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
