#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The verdict record on <c>Admin/UpdatePolicy</c> — where the Candidate Release Protocol's
/// instance gate lands "cannot update to X" so an admin actually sees it. Against a REAL mesh:
/// <see cref="UpdatePolicyNodeType.RecordVerification"/> writes through the node stream (the only
/// mutation API), the content round-trips the hub serializer (nested records + enums — the shape
/// an operator's <c>combo-verdict.json</c> patch must survive), upsert-by-tag never duplicates,
/// and the list is bounded. The rendering tests are pure: the three verdicts and the join against
/// <c>LatestAvailableTag</c>.
/// </summary>
public class ComboVerdictRecordingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Tag = "3.0.0-ci.99";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ── recording ──

    [Fact(Timeout = 60000)]
    public async Task RecordVerification_RoundTripsTheWholeVerdict_ThroughTheHubSerializer()
    {
        await Seed();
        var verdict = RedVerdict(Tag) with
        {
            Caveats = ["'Store' was materialised from a MOVING ref"],
        };

        await Record(verdict);

        // A cross-hub Update completes optimistically — wait for the owner's reconciled state to
        // CARRY the verdict, never a bare first emission (which can be the pre-write snapshot).
        var content = await WaitForContent(c => c.VerificationFor(Tag) is not null);
        var read = content.VerificationFor(Tag);
        read.Should().NotBeNull();
        read!.Verdict.Should().Be(ComboVerdictKind.Red);
        read.ImageRef.Should().Be(verdict.ImageRef);
        read.ImageDigest.Should().Be(verdict.ImageDigest);
        read.Caveats.Should().ContainSingle().Which.Should().Contain("MOVING");
        var module = read.Modules.Single(m => m.ModuleId == "Widget");
        module.Outcome.Should().Be(ModuleVerificationOutcome.Failed);
        module.ResolvedCommit.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        module.Failures.Should().ContainSingle().Which.Should().Contain("AddTracking");
        content.Policy.Should().Be(UpdatePolicyKind.Stable, "the admin-chosen policy is preserved");
    }

    [Fact(Timeout = 60000)]
    public async Task RecordVerification_UpsertsByTag_ARerunReplacesRatherThanDuplicates()
    {
        await Seed();
        await Record(RedVerdict(Tag));
        await Record(GreenVerdict(Tag) with { VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(1) });

        var content = await WaitForContent(c => c.VerificationFor(Tag)?.Verdict == ComboVerdictKind.Green);
        content.ComboVerifications.Should().ContainSingle(
            v => string.Equals(v.CandidateTag, Tag, StringComparison.OrdinalIgnoreCase),
            "a re-verification of the same candidate replaces its verdict");
        content.VerificationFor(Tag)!.Verdict.Should().Be(ComboVerdictKind.Green);
    }

    [Fact(Timeout = 60000)]
    public async Task RecordVerification_IsBounded_NewestKept_TheNodeNeverGrowsWithoutLimit()
    {
        await Seed();
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < UpdatePolicyNodeType.MaxRecordedVerifications + 2; i++)
            await Record(GreenVerdict($"3.0.0-ci.{i}") with { VerifiedAt = baseTime.AddMinutes(i) });

        // Wait for the LAST write to be reconciled — the tell of the read-after-write race is the
        // newest verdict missing from a bare-first-emission read.
        var lastTag = $"3.0.0-ci.{UpdatePolicyNodeType.MaxRecordedVerifications + 1}";
        var content = await WaitForContent(c => c.VerificationFor(lastTag) is not null);
        content.ComboVerifications.Should().HaveCount(UpdatePolicyNodeType.MaxRecordedVerifications);
        content.ComboVerifications[0].CandidateTag.Should().Be(
            $"3.0.0-ci.{UpdatePolicyNodeType.MaxRecordedVerifications + 1}", "newest first");
        content.VerificationFor("3.0.0-ci.0").Should().BeNull("the oldest fell off");
        content.VerificationFor("3.0.0-ci.1").Should().BeNull();
    }

    // ── rendering: the join an admin actually sees ──

    [Fact]
    public void StatusMarkdown_RedVerdict_ReplacesUpdateAvailable_NamesEveryFailingModule()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            CheckedAt = DateTimeOffset.UtcNow,
            ComboVerifications =
            [
                RedVerdict(Tag) with
                {
                    Modules =
                    [
                        FailedModule("Widget", "Widget/Thing: compile failed — no 'AddTracking'"),
                        FailedModule("Store", "install: import faulted"),
                        PassedModule("Edu"),
                    ],
                    Caveats = ["'Store' has diverged from its install record"],
                },
            ],
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        markdown.Should().Contain($"ui.updateBlocked[{Tag}]",
            "a blocked upgrade must not read as a plain 'update available'");
        markdown.Should().NotContain("ui.updateLatestAvailable");
        markdown.Should().Contain("**Widget**").And.Contain("AddTracking");
        markdown.Should().Contain("**Store**").And.Contain("import faulted");
        markdown.Should().NotContain("**Edu**", "only failing modules are listed");
        markdown.Should().Contain("ui.updateVerifiedAtLine");
        markdown.Should().Contain("ui.updateCaveats").And.Contain("diverged",
            "caveats are mandatory-to-surface on EVERY verdict, red included");
    }

    [Fact]
    public void StatusMarkdown_GreenWithCaveats_NeverRendersAsAnUnqualifiedPass()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            ComboVerifications =
            [
                GreenVerdict(Tag) with
                {
                    Caveats = ["'Widget' was materialised from a MOVING ref"],
                },
            ],
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        markdown.Should().Contain("ui.updateVerifiedGreen[1]");
        markdown.Should().Contain("ui.updateCaveats").And.Contain("MOVING",
            "a green over a moving pin must not read as reproducible evidence");
    }

    [Fact]
    public void StatusMarkdown_GreenVerdict_SaysVerified_KeepsTheAvailableLine()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            ComboVerifications = [GreenVerdict(Tag)],
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        markdown.Should().Contain($"ui.updateLatestAvailable[{Tag}]");
        markdown.Should().Contain("ui.updateVerifiedGreen[1]", "one module verified");
    }

    [Fact]
    public void StatusMarkdown_NotVerifiable_SurfacesTheCaveats()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            ComboVerifications =
            [
                GreenVerdict(Tag) with
                {
                    Verdict = ComboVerdictKind.NotVerifiable,
                    Caveats = ["the tester produced no structured report (exit 2)"],
                    Modules =
                    [
                        PassedModule("Widget") with
                        {
                            Outcome = ModuleVerificationOutcome.NotVerified,
                            Failures = ["Refused: pinned only to a moving ref"],
                        },
                    ],
                },
            ],
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        markdown.Should().Contain($"ui.updateNotVerifiable[{Tag}]");
        markdown.Should().Contain("no structured report",
            "a partial answer must never read as a healthy one");
        markdown.Should().Contain("**Widget**").And.Contain("moving ref",
            "the per-module reason names WHICH module prevented verification");
    }

    [Fact]
    public void StatusMarkdown_NoVerdictForTheTag_RendersThePlainAvailableLine()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            // A verdict for a DIFFERENT tag must not decorate this one.
            ComboVerifications = [RedVerdict("2.0.0-ci.1")],
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        markdown.Should().Contain($"ui.updateLatestAvailable[{Tag}]");
        markdown.Should().NotContain("ui.updateBlocked");
    }

    [Fact]
    public void StatusMarkdown_NoTagAtAll_SaysNothingDetected()
    {
        UpdatePolicySettingsTab.StatusMarkdown(new UpdatePolicyContent(), EchoLocalizer)
            .Should().Be("ui.updateNoNewerVersion[]");
    }

    // ── helpers ──

    /// <summary>A localizer echoing key + args, so assertions pin WHICH key renders (the real
    /// catalog is guarded by LocalizationTest).</summary>
    private static string EchoLocalizer(string key, object?[] args) =>
        $"{key}[{string.Join(',', args)}]";

    private Task Seed()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Stable },
        };
        // System scope opened/closed SYNCHRONOUSLY around the subscribe — the
        // NonAdminUpdateStatusTest shape, for the same AsyncLocal-leak reason.
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .ToTask(TestContext.Current.CancellationToken);
    }

    private Task Record(ComboVerification verdict) =>
        UpdatePolicyNodeType.RecordVerification(Mesh, verdict)
            .FirstAsync()
            .Timeout(Budget)
            .ToTask(TestContext.Current.CancellationToken);

    /// <summary>The first reconciled content matching <paramref name="predicate"/> — the
    /// wait-on-the-condition read (never a bare first emission, which can predate the write).</summary>
    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> predicate) =>
        Observable.Create<UpdatePolicyContent>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Where(node => node is not null)
                        .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                        .Subscribe(observer);
            })
            .Where(predicate)
            .FirstAsync()
            .Timeout(Budget)
            .ToTask(TestContext.Current.CancellationToken);

    private static ComboVerification GreenVerdict(string tag) => new()
    {
        CandidateTag = tag,
        ImageRef = $"meshweaver.azurecr.io/memex-portal-ai:{tag}",
        ImageDigest = "meshweaver.azurecr.io/memex-portal-ai@sha256:feedc0de",
        VerifiedAt = DateTimeOffset.UtcNow,
        ComboReadAt = DateTimeOffset.UtcNow,
        Verdict = ComboVerdictKind.Green,
        Modules = [PassedModule("Widget")],
    };

    private static ComboVerification RedVerdict(string tag) => GreenVerdict(tag) with
    {
        Verdict = ComboVerdictKind.Red,
        Modules = [FailedModule("Widget", "Widget/Thing: compile failed — no 'AddTracking'")],
    };

    private static ModuleVerification PassedModule(string id) => new()
    {
        ModuleId = id,
        Outcome = ModuleVerificationOutcome.Passed,
        ResolvedCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        ContentHash = new string('1', 64),
        Pin = MaterializationPin.ExactCommit,
    };

    private static ModuleVerification FailedModule(string id, string failure) =>
        PassedModule(id) with
        {
            Outcome = ModuleVerificationOutcome.Failed,
            Failures = [failure],
        };
}
