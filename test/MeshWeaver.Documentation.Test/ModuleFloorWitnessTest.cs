#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A package's declared floor is read as a fact only when its witness (<c>mesh-floor.lock</c>)
/// vouches for EXACTLY the incoming sources — and on an instance that is behind, a floor nobody
/// stamped for them holds the module (<see cref="ModuleSyncDecision.HoldUnverifiedFloors"/>).
/// Measured 2026-10-09 on memex.systemorph.com: MeshWeaver.Plugins@73e5065d carried the approvals
/// inbox row selection (Plugins#3214) while <c>Hosting</c> still declared its previous sources'
/// floor <c>3.0.0-ci.10305</c>; GitSync synced it onto 3.0.0-ci.10310, whose image had no renderer
/// for the selection.
///
/// <para>The hash half is pinned against the node repository's OWN witnesses: two real packages
/// copied byte for byte from MeshWeaver.Plugins@0b8f8754 (<c>FloorWitnessFixtures/</c>; AzureBlob is
/// a mixed package whose lock records out-of-folder <c>src/</c> entries), and one synthetic package
/// whose expected hash was produced by <c>mesh-floors.py</c>'s own algorithm (floats, non-ASCII,
/// escapes, a binary file, excluded names at depth).</para>
/// </summary>
public class ModuleFloorWitnessTest
{
    private const string Running = "3.0.0-ci.10310";
    private const string Newer = "3.0.0-ci.10319";

    // ── the hash: recomputed exactly as the stamp computed it ────────────────────────────────

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("Grok")]
    public void TheRecomputedHash_EqualsTheStampsOwnWitness_OnRealPackages(string package)
    {
        var files = Fixture(package);
        var reading = ModuleSyncDecision.Read(files).Single();

        reading.Witness.Should().NotBeNull();
        reading.IncomingContentHash.Should().Be(reading.Witness!.ContentHash,
            $"{package} at MeshWeaver.Plugins@0b8f8754 is not pending (mesh-floors.py pending: 0 of 76), so "
            + "the hash of its sources must be the one its witness recorded");
        reading.FloorVerified.Should().BeTrue();
    }

    /// <summary>NEGATIVE CONTROL for the hash: one byte changed in one source ⇒ not verified.</summary>
    [Fact]
    public void OneChangedSourceByte_IsNotVerified()
    {
        var files = Fixture("Grok")
            .Select(f => f.Path == "Harness/Grok.json" ? f with { Content = f.Content.Replace("Grok", "Grak") } : f)
            .ToList();

        ModuleSyncDecision.Read(files).Single().FloorVerified.Should().BeFalse(
            "the sources moved after the stamp, so the witness vouches for other content");
    }

    /// <summary>The floor's own value is NOT part of the hash: the stamp writes it after it hashed.</summary>
    [Fact]
    public void ChangingOnlyTheFloorValue_KeepsTheFloorVerified()
    {
        var files = Fixture("Grok")
            .Select(f => f.Path == "index.json"
                ? f with { Content = System.Text.RegularExpressions.Regex.Replace(
                    f.Content, "\"minMeshVersion\"\\s*:\\s*\"[^\"]*\"", "\"minMeshVersion\": \"3.0.0-ci.99999\"") }
                : f)
            .ToList();

        var reading = ModuleSyncDecision.Read(files).Single();
        reading.Floor.Should().Be("3.0.0-ci.99999");
        reading.FloorVerified.Should().BeTrue();
    }

    [Fact]
    public void TheSyntheticPackage_HashesAsMeshFloorsPyDoes()
    {
        const string index =
            """{"nodeType":"Space","name":"Synth","zeta":1,"alpha":[2.5,1e16,1.5e-05,0.0,-3,true,null],"content":{"minMeshVersion":"3.0.0-ci.10317","price":0.0,"title":"Zürich ✓","esc":"a\"b\\c\n\u0001\t","nested":{"b":1,"a":{"y":2,"x":100000.0}}}}""";
        const string code = "// <meshweaver>\n// Id: X\n// </meshweaver>\nclass X { }\n";
        var manifest = """
            {"module": "Synth", "moduleVersion": "0000000000000000", "files": {
              "Synth/index.json": "ignored-recomputed",
              "Synth/Source/X.cs": "ignored-recomputed",
              "src/Synth.Module/A.cs": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "src/Synth.Module/Synth.Module.csproj": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}
            """;
        RepoFile[] files =
        [
            new("index.json", index),
            new("manifest.lock", manifest),
            new("mesh-floor.lock", """{"schema":"mw-mesh-floor/1","contentHash":"f8277538f836aecd","verifiedOn":"3.0.0-ci.10317"}"""),
            new("Source/X.cs", code),
            new("img.bin", "", [0xff, 0x00, 0x10]),
            new("sub/manifest.lock", "excluded by name at any depth"),
            new(".DS_Store", "excluded by name"),
        ];

        // Expected: python3 — the content_hash rule of MeshWeaver.Plugins scripts/mesh-floors.py over
        // exactly these files (the PR body carries the script).
        ModuleSyncDecision.Read(files).Single(m => m.Root.Length == 0)
            .IncomingContentHash.Should().Be("f8277538f836aecd");
    }

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(2.5, "2.5")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e-05, "1.5e-05")]
    [InlineData(100000.0, "100000.0")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(3.14159, "3.14159")]
    [InlineData(1e-07, "1e-07")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    public void Floats_RenderAsPythonRepr(double value, string python)
        => ModuleFloorWitness.PythonJson.FloatRepr(value).Should().Be(python);

    [Fact]
    public void NoWitness_OrNoLock_IsNotJudged()
    {
        ModuleSyncDecision.Read(Fixture("Grok").Where(f => f.Path != "mesh-floor.lock").ToList())
            .Single().FloorVerified.Should().BeNull("a tree with no witness states nothing about its floor");
        ModuleFloorWitness.ContentHash([("index.json", Encoding.UTF8.GetBytes("{}"))], "{ not json")
            .Should().BeNull();
    }

    // ── the rule: hold an unverified floor on an instance that is behind ─────────────────────

    private static ModuleReading Reading(bool? verified)
        => new("Hosting", "", "eba9cf1fa248d6f4", "3.0.0-ci.10305")
        {
            Witness = verified is null ? null : new FloorWitness("7d2bb1d8c1b7b21f", "3.0.0-ci.10305"),
            IncomingContentHash = verified switch
            {
                true => "7d2bb1d8c1b7b21f",
                false => "705673bfc0b02ea1",
                null => null,
            },
        };

    private static ImmutableList<ModuleSyncOutcome> Judge(ModuleReading reading, string? newer)
        => ModuleSyncDecision.HoldUnverifiedFloors(
            ModuleSyncDecision.Decide([reading], new Dictionary<string, string> { ["Hosting"] = "a052a49965a441ce" },
                Running, reconcile: false),
            [reading], Running, newer);

    /// <summary>THE repro: a stale floor (stamped for other content) on an instance behind a newer
    /// platform is declined, the reason names what it holds and why, and the held hash stays.</summary>
    [Fact]
    public void AnUnverifiedFloor_OnALaggingInstance_IsHeld_AndTheOldBuildKeepsServing()
    {
        var outcome = Judge(Reading(verified: false), Newer).Single();

        outcome.Outcome.Should().Be(ModuleSyncOutcomeKind.Declined);
        outcome.FloorUnverified.Should().BeTrue();
        outcome.Floor.Should().Be("3.0.0-ci.10305",
            "the outcome keeps the DECLARED floor — the module does not claim a newer platform");
        outcome.AvailablePlatform.Should().Be(Newer);
        outcome.Reason.Should().Contain("3.0.0-ci.10305").And.Contain(Running).And.Contain(Newer)
            .And.Contain("705673bfc0b02ea1").And.Contain("7d2bb1d8c1b7b21f");
        ModuleSyncDecision.Recorded([outcome])["Hosting"].Should().Be("a052a49965a441ce",
            "a declined module did not land — the Space keeps the hash it held, so its files stay in the next diff");
    }

    /// <summary>NEGATIVE CONTROL (policy sources-sync-on-push): the same stale floor on an instance
    /// that runs the newest platform it knows of syncs — nothing waits for a stamp there.</summary>
    [Fact]
    public void AnUnverifiedFloor_OnAnInstanceRunningTheNewestPlatform_Syncs()
        => Judge(Reading(verified: false), newer: null).Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    /// <summary>A VERIFIED floor at or below the running platform syncs even on a lagging instance —
    /// only an unverified floor is held there.</summary>
    [Fact]
    public void AVerifiedFloor_AtOrBelowTheRunningPlatform_Syncs_EvenWhenANewerOneIsAvailable()
        => Judge(Reading(verified: true), Newer).Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    /// <summary>A verified floor ABOVE the running platform is still declined by the floor rule.</summary>
    [Fact]
    public void AVerifiedFloorAboveTheRunningPlatform_IsDeclinedByTheFloorRule()
    {
        var reading = Reading(verified: true) with { Floor = "3.0.0-ci.10317" };
        var outcome = Judge(reading, Newer).Single();
        outcome.Outcome.Should().Be(ModuleSyncOutcomeKind.Declined);
        outcome.FloorUnverified.Should().BeFalse();
        outcome.Floor.Should().Be("3.0.0-ci.10317");
    }

    /// <summary>The activity names an unverified-floor hold with its OWN localized message and the
    /// platforms as arguments — never as a floor decline.</summary>
    [Fact]
    public void TheActivityNamesAnUnverifiedFloorHold_ApartFromAFloorDecline()
    {
        var result = new MeshWeaver.Graph.StaticRepoImportResult("Hosting", "manifest:Hosting=eba9", "Declined")
        {
            UnverifiedFloorModules = ["Hosting"],
            UnverifiedFloorAvailablePlatform = Newer,
        };

        var line = GitHubActivityExtensions.ModulesFloorUnverifiedLine(result);
        line.Should().NotBeNull();
        line!.Message.Should().Contain("Hosting").And.Contain(Newer).And.Contain("stamped");
        GitHubActivityExtensions.ModulesFloorUnverifiedLine(
                new MeshWeaver.Graph.StaticRepoImportResult("Hosting", "x", "Skipped"))
            .Should().BeNull("an import that held nothing this way says nothing about it");
    }

    /// <summary>A reading nothing can verify (no witness) is not judged — as before the rule.</summary>
    [Fact]
    public void AnUnjudgeableFloor_Syncs()
        => Judge(Reading(verified: null), Newer).Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    /// <summary>An unchanged module writes nothing whatever its witness says.</summary>
    [Fact]
    public void AnUnchangedModule_StaysUnchanged()
    {
        var reading = Reading(verified: false) with { ModuleVersion = "a052a49965a441ce" };
        Judge(reading, Newer).Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Unchanged);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One copied package, Space-relative (the Space IS the package), with its raw bytes
    /// decoded as the transports decode them (strict UTF-8).</summary>
    private static List<RepoFile> Fixture(string package)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "FloorWitnessFixtures", package);
        Directory.Exists(root).Should().BeTrue($"the fixture {root} is copied to the output directory");
        return Directory.EnumerateFiles(root, "*.fixture", SearchOption.AllDirectories)
            .Select(full =>
            {
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                var bytes = File.ReadAllBytes(full);
                return new RepoFile(relative[..^".fixture".Length],
                    new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
            })
            .ToList();
    }
}
