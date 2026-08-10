#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The combo assembler (step 2 of the Candidate Release Protocol's instance gate): a synthetic
/// <see cref="InstanceCombo"/> against in-memory fixture repos — no network, no mesh. Pins the
/// three contracts the gate depends on: moving/unrecorded refs are REFUSED by default (and the
/// refusal names the module), the materialised layout is exactly the repo root
/// <c>mw-plugin-test</c> discovers (<c>&lt;ModuleId&gt;/index.json</c> at the top level), and the
/// manifest names every module's resolved ref + content hash so the verdict can name its input.
/// </summary>
public class InstanceComboAssemblerTest
{
    private const string WidgetSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PluginsSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","nodeType":"Space","name":"Widget"}""";

    private const string StoreIndexJson =
        """{"$type":"MeshNode","id":"Store","nodeType":"Space","name":"Store"}""";

    // ── in-memory fixture repos, keyed (repoUrl, gitRef) — the fetch seam's fake ──

    private sealed class FakeRepos
    {
        private readonly Dictionary<string, RepoSnapshot> snapshots = new(StringComparer.Ordinal);
        private int fetchCount;

        public int FetchCount => Volatile.Read(ref fetchCount);

        public void Add(string repoUrl, string gitRef, string sha,
            params (string Path, string Content)[] files) =>
            snapshots[$"{repoUrl}|{gitRef}"] = new RepoSnapshot(sha,
                files.Select(f => new RepoFile(f.Path, f.Content)).ToImmutableList());

        /// <summary>The fetch delegate — mirrors the real client's subdirectory semantics
        /// (only files under it, paths made relative to it).</summary>
        public IObservable<RepoSnapshot> Fetch(
            string repoUrl, string gitRef, string? subdirectory, string _)
        {
            Interlocked.Increment(ref fetchCount);
            if (!snapshots.TryGetValue($"{repoUrl}|{gitRef}", out var snapshot))
                return Observable.Throw<RepoSnapshot>(
                    new InvalidOperationException($"no fixture for {repoUrl} @ {gitRef}"));
            if (subdirectory is not { Length: > 0 })
                return Observable.Return(snapshot);
            var prefix = subdirectory.TrimEnd('/') + "/";
            return Observable.Return(new RepoSnapshot(snapshot.CommitSha, snapshot.Files
                .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new RepoFile(f.Path[prefix.Length..], f.Content, f.Binary))
                .ToImmutableList()));
        }
    }

    [Fact]
    public async Task MovingOrUnrecordedRefs_RefusedByDefault_NothingFetchedNothingWritten()
    {
        var repos = new FakeRepos();
        var combo = Combo(
            // Pinned only to a branch — MOVING.
            new ModuleCoordinate
            {
                ModuleId = "Drifting",
                GitSync = new GitSyncCoordinate
                {
                    RepositoryUrl = "https://example.test/drifting",
                    Branch = "main",
                },
            },
            // A repo but no ref at all — UNRECORDED.
            new ModuleCoordinate
            {
                ModuleId = "Pinless",
                GitSync = new GitSyncCoordinate { RepositoryUrl = "https://example.test/pinless" },
            });
        var workRoot = TempDir();
        try
        {
            var report = await Assemble(repos, combo, workRoot);

            report.Success.Should().BeFalse();
            report.ExitCode.Should().Be(1);
            report.Modules.Should().HaveCount(2);
            var drifting = report.Modules.Single(m => m.ModuleId == "Drifting");
            drifting.Status.Should().Be(ModuleAssemblyStatus.Refused);
            drifting.Fidelity.Should().Be(RefFidelity.Moving);
            drifting.Error.Should().Contain("Drifting").And.Contain("--allow-moving");
            var pinless = report.Modules.Single(m => m.ModuleId == "Pinless");
            pinless.Status.Should().Be(ModuleAssemblyStatus.Refused);
            pinless.Fidelity.Should().Be(RefFidelity.Unrecorded);
            pinless.Error.Should().Contain("Pinless");

            // Refusal happens at PLANNING — before any network or module write.
            repos.FetchCount.Should().Be(0);
            Directory.EnumerateFileSystemEntries(workRoot).Should().ContainSingle()
                .Which.Should().EndWith(InstanceComboAssembler.ManifestFileName);
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task ExactRefs_MaterializeTesterLayout_ManifestNamesResolvedRefAndHash()
    {
        var repos = new FakeRepos();
        // A GitSync-coordinate module: subdirectory fetch at the recorded sync sha.
        repos.Add("https://example.test/widget-repo", WidgetSha, WidgetSha,
            ("Widget/index.json", WidgetIndexJson),
            ("Widget/Thing.json", """{"id":"Thing","nodeType":"NodeType"}"""),
            ("Widget/Thing/Source/Thing.cs", "public record Thing;"),
            ("README.md", "# outside the subdirectory — must NOT be materialised"));
        // A package-coordinate module: whole-repo fetch at the installed sha, folder-filtered.
        repos.Add("https://example.test/plugins-repo", PluginsSha, PluginsSha,
            ("Store/index.json", StoreIndexJson),
            ("Store/Catalog.json", """{"id":"Catalog","nodeType":"NodeType"}"""),
            ("Other/index.json", """{"id":"Other","nodeType":"Space"}"""),
            ("README.md", "# repo readme — not part of any module"));
        var combo = Combo(
            new ModuleCoordinate
            {
                ModuleId = "Widget",
                GitSync = new GitSyncCoordinate
                {
                    RepositoryUrl = "https://example.test/widget-repo",
                    Branch = "main",
                    Subdirectory = "Widget",
                    LastSyncCommitSha = WidgetSha,
                },
            },
            new ModuleCoordinate
            {
                ModuleId = "Store",
                Package = new PackageCoordinate
                {
                    PackageId = "Store",
                    SourceName = "plugins",
                    InstalledFromRef = PluginsSha,
                },
            });
        var options = new ComboAssemblyOptions
        {
            SourceRepositories = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("plugins", "https://example.test/plugins-repo"),
        };
        var workRoot = TempDir();
        var secondRoot = TempDir();
        try
        {
            var report = await Assemble(repos, combo, workRoot, options);

            report.FatalError.Should().BeNull();
            report.Success.Should().BeTrue($"both modules are exactly pinned; report: {Dump(report)}");
            report.ExitCode.Should().Be(0);
            repos.FetchCount.Should().Be(2, "one fetch per (repo, ref)");

            // The layout is the repo root mw-plugin-test discovers: <ModuleId>/index.json.
            File.ReadAllText(Path.Combine(workRoot, "Widget", "index.json"))
                .Should().Be(WidgetIndexJson);
            File.Exists(Path.Combine(workRoot, "Widget", "Thing", "Source", "Thing.cs"))
                .Should().BeTrue();
            File.ReadAllText(Path.Combine(workRoot, "Store", "index.json"))
                .Should().Be(StoreIndexJson);
            Directory.Exists(Path.Combine(workRoot, "Other"))
                .Should().BeFalse("only the combo's modules are materialised");
            File.Exists(Path.Combine(workRoot, "README.md"))
                .Should().BeFalse("repo files outside a module folder are not materialised");

            var widget = report.Modules.Single(m => m.ModuleId == "Widget");
            widget.Status.Should().Be(ModuleAssemblyStatus.Materialized);
            widget.Pin.Should().Be(MaterializationPin.ExactCommit);
            widget.ResolvedCommit.Should().Be(WidgetSha);
            widget.FileCount.Should().Be(3);
            widget.DeclaresRoot.Should().BeTrue();
            widget.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
            var store = report.Modules.Single(m => m.ModuleId == "Store");
            store.Pin.Should().Be(MaterializationPin.ExactCommit);
            store.ResolvedCommit.Should().Be(PluginsSha);
            store.FileCount.Should().Be(2);

            // The MANIFEST file carries the same entries — module → resolved ref → content hash —
            // in the documented shape (web casing, enums as strings).
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workRoot, InstanceComboAssembler.ManifestFileName)));
            var entries = manifest.RootElement.GetProperty("modules").EnumerateArray()
                .ToDictionary(e => e.GetProperty("moduleId").GetString()!);
            entries.Should().ContainKey("Widget").And.ContainKey("Store");
            entries["Widget"].GetProperty("resolvedCommit").GetString().Should().Be(WidgetSha);
            entries["Widget"].GetProperty("status").GetString().Should().Be("Materialized");
            entries["Widget"].GetProperty("pin").GetString().Should().Be("ExactCommit");
            entries["Store"].GetProperty("contentHash").GetString()
                .Should().Be(store.ContentHash);

            // Content-addressed: assembling the same combo again yields the same hashes.
            var again = await Assemble(repos, combo, secondRoot, options);
            again.Modules.Single(m => m.ModuleId == "Widget").ContentHash
                .Should().Be(widget.ContentHash);
            again.Modules.Single(m => m.ModuleId == "Store").ContentHash
                .Should().Be(store.ContentHash);
        }
        finally
        {
            TryDelete(workRoot);
            TryDelete(secondRoot);
        }
    }

    [Fact]
    public async Task AllowMoving_MaterializesBranchPinnedModule_StampedAsMoving()
    {
        var repos = new FakeRepos();
        repos.Add("https://example.test/drifting", "main", WidgetSha,
            ("index.json", WidgetIndexJson));
        var combo = Combo(new ModuleCoordinate
        {
            ModuleId = "Drifting",
            GitSync = new GitSyncCoordinate
            {
                RepositoryUrl = "https://example.test/drifting",
                Branch = "main",
            },
        });
        var workRoot = TempDir();
        try
        {
            var report = await Assemble(repos, combo, workRoot,
                new ComboAssemblyOptions { AllowMoving = true });

            var module = report.Modules.Single();
            module.Status.Should().Be(ModuleAssemblyStatus.Materialized);
            module.Pin.Should().Be(MaterializationPin.Moving,
                "the manifest must stamp an unpinned materialisation so it can never read as evidence");
            report.AllowMoving.Should().BeTrue();
            report.Success.Should().BeTrue();
            File.Exists(Path.Combine(workRoot, "Drifting", "index.json")).Should().BeTrue();
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task RecordedModuleVersion_WithMovingRef_ProvesItselfAgainstManifestLock()
    {
        var repos = new FakeRepos();
        repos.Add("https://example.test/plugins-repo", "main", PluginsSha,
            ("Edu/index.json", StoreIndexJson),
            ("Edu/manifest.lock",
                """{"module":"Edu","moduleVersion":"mv-123","files":{"Edu/index.json":"deadbeef"}}"""));
        var options = new ComboAssemblyOptions
        {
            DefaultSourceRepository = "https://example.test/plugins-repo",
        };

        ModuleCoordinate Module(string recordedVersion) => new()
        {
            ModuleId = "Edu",
            Package = new PackageCoordinate
            {
                PackageId = "Edu",
                ModuleVersion = recordedVersion,
                InstalledFromRef = "main",
            },
        };

        var matchRoot = TempDir();
        var mismatchRoot = TempDir();
        try
        {
            // The recorded content hash matches the fetched tree's manifest.lock: the movable ref
            // is PROVEN and needs no --allow-moving.
            var match = await Assemble(repos, Combo(Module("mv-123")), matchRoot, options);
            var proven = match.Modules.Single();
            proven.Status.Should().Be(ModuleAssemblyStatus.Materialized);
            proven.Pin.Should().Be(MaterializationPin.VerifiedModuleVersion);
            proven.ModuleVersionMatches.Should().BeTrue();
            match.Success.Should().BeTrue();

            // The branch moved past the recorded install: the materialisation would not be the
            // recorded input — a NAMED failure, and nothing is written for the module.
            var mismatch = await Assemble(repos, Combo(Module("mv-999")), mismatchRoot, options);
            var moved = mismatch.Modules.Single();
            moved.Status.Should().Be(ModuleAssemblyStatus.Failed);
            moved.Error.Should().Contain("mv-999").And.Contain("mv-123");
            Directory.Exists(Path.Combine(mismatchRoot, "Edu")).Should().BeFalse();
            mismatch.ExitCode.Should().Be(1);
        }
        finally
        {
            TryDelete(matchRoot);
            TryDelete(mismatchRoot);
        }
    }

    [Fact]
    public async Task FetchFailure_IsNamedForItsModules_OthersStillMaterialize()
    {
        var repos = new FakeRepos();
        // Only the good repo exists; the other fetch faults.
        repos.Add("https://example.test/good", WidgetSha, WidgetSha,
            ("index.json", WidgetIndexJson));
        var combo = Combo(
            new ModuleCoordinate
            {
                ModuleId = "Good",
                GitSync = new GitSyncCoordinate
                {
                    RepositoryUrl = "https://example.test/good",
                    LastSyncCommitSha = WidgetSha,
                },
            },
            new ModuleCoordinate
            {
                ModuleId = "Gone",
                GitSync = new GitSyncCoordinate
                {
                    RepositoryUrl = "https://example.test/gone",
                    LastSyncCommitSha = PluginsSha,
                },
            });
        var workRoot = TempDir();
        try
        {
            var report = await Assemble(repos, combo, workRoot);

            // Breadth-complete: the failure is named for exactly the module riding that fetch,
            // and the other module still materialises.
            var gone = report.Modules.Single(m => m.ModuleId == "Gone");
            gone.Status.Should().Be(ModuleAssemblyStatus.Failed);
            gone.Error.Should().Contain("https://example.test/gone");
            report.Modules.Single(m => m.ModuleId == "Good").Status
                .Should().Be(ModuleAssemblyStatus.Materialized);
            report.Success.Should().BeFalse();
            report.ExitCode.Should().Be(1);
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task IncompleteCombo_RefusedByDefault_AssembledOnlyWithAllowFlag()
    {
        var repos = new FakeRepos();
        repos.Add("https://example.test/good", WidgetSha, WidgetSha,
            ("index.json", WidgetIndexJson));
        var combo = Combo(new ModuleCoordinate
        {
            ModuleId = "Good",
            GitSync = new GitSyncCoordinate
            {
                RepositoryUrl = "https://example.test/good",
                LastSyncCommitSha = WidgetSha,
            },
        }) with
        {
            IsComplete = false,
            Caveats = ["Could not read install records: boom."],
        };
        var refusedRoot = TempDir();
        var allowedRoot = TempDir();
        try
        {
            // "This instance carries nothing" and "we could not find out" must never conflate:
            // a known-short combo is refused outright, before any fetch.
            var refused = await Assemble(repos, combo, refusedRoot);
            refused.FatalError.Should().Contain("INCOMPLETE");
            refused.Success.Should().BeFalse();
            refused.Modules.Should().BeEmpty();
            repos.FetchCount.Should().Be(0);
            // The manifest still documents WHY nothing was materialised.
            File.ReadAllText(Path.Combine(refusedRoot, InstanceComboAssembler.ManifestFileName))
                .Should().Contain("INCOMPLETE");

            var allowed = await Assemble(repos, combo, allowedRoot,
                new ComboAssemblyOptions { AllowIncomplete = true });
            allowed.FatalError.Should().BeNull();
            allowed.Success.Should().BeTrue();
            allowed.ComboIsComplete.Should().BeFalse("the combo's own claim is carried through");
        }
        finally
        {
            TryDelete(refusedRoot);
            TryDelete(allowedRoot);
        }
    }

    // ── helpers ──

    private static Task<ComboAssemblyReport> Assemble(
        FakeRepos repos, InstanceCombo combo, string workRoot,
        ComboAssemblyOptions? options = null) =>
        new InstanceComboAssembler(repos.Fetch, IoPool.Unbounded, options)
            .Assemble(combo, workRoot)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

    private static InstanceCombo Combo(params ModuleCoordinate[] modules) => new()
    {
        ReadAt = DateTimeOffset.UtcNow,
        Modules = modules.ToImmutableList(),
    };

    private static string Dump(ComboAssemblyReport report)
    {
        var writer = new StringWriter();
        report.WriteSummary(writer);
        return writer.ToString();
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "mw-combo-fixture-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort — the OS reclaims temp at reboot
        }
    }
}
