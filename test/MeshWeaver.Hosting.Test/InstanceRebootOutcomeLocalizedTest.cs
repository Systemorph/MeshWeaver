using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#7322 — an <c>Admin/_Reboot/{id}</c> request stored its outcome (<c>Failure</c>, each
/// step's and each check's <c>Detail</c>, the <c>Log</c>) as English prose, and its page was the
/// generic content view, which rendered that prose verbatim: a German viewer read English. Each
/// sentence now carries a keyed form (<see cref="RebootText"/>) beside the English, and the request
/// has its own page (<see cref="InstanceRebootLayoutAreas"/>) that renders the keyed form in the
/// VIEWER's language. The English stays the wire value — and the <c>stalled: </c> machine marker the
/// watchdog reads back stays byte-stable.
/// </summary>
public class InstanceRebootOutcomeLocalizedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly DateTimeOffset Restart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddPluginCatalog();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>A request waiting for its restart, past its window: what the reconcile pass ends as stalled.</summary>
    private static InstanceRebootRequest AwaitingRestart() => new()
    {
        Reason = "test",
        RequestedBy = "someone",
        RequestedAt = Restart,
        Status = InstanceRebootStatus.AwaitingRestart,
        Steps = InstanceReboot.FreshSteps(),
        RestartRequestedAt = Restart,
        ExecutionWindow = Window,
        ProgressAt = Restart,
    };

    private static InstanceRebootRequest EndedStalled() =>
        InstanceReboot.EndIfStalled(AwaitingRestart() with { RequestedEndIfStalledAt = Restart.AddHours(2) },
            Restart.AddHours(2), Window, TimeSpan.Zero);

    /// <summary>
    /// The NEGATIVE CONTROL for the keys: the English Failure — and with it the watchdog's
    /// <see cref="InstanceReboot.StalledMarker"/> — is byte-for-byte what it was before the keys
    /// existed, and <see cref="InstanceReboot.EndedAsStalled"/> still reads the stall back.
    /// </summary>
    [Fact]
    public void TheStalledMarker_StaysByteStable()
    {
        const string why = "status AwaitingRestart, no progress since 2026-01-01 00:00:00Z — longer than its declared "
                           + "execution window of 30 min; the restart was requested at 2026-01-01 00:00:00Z and no "
                           + "process booted after it has reported its checks";
        InstanceReboot.StalledMarker.Should().Be("stalled: ");
        var ended = EndedStalled();
        ended.Status.Should().Be(InstanceRebootStatus.Failed);
        ended.Failure.Should().Be("stalled: " + why);
        InstanceReboot.EndedAsStalled(ended).Should().Be(why);
        ended.Log[^1].Should().Be("2026-01-01 02:00:00Z ended as Failed — stalled: " + why);

        // A prior failure is kept in front, joined as before.
        var prior = InstanceReboot.EndIfStalled(AwaitingRestart() with { Failure = "Sync: boom" }, Restart.AddHours(2), Window, TimeSpan.Zero);
        prior.Failure.Should().Be("Sync: boom; stalled: " + why);
        InstanceReboot.EndedAsStalled(prior).Should().Be(why);
    }

    /// <summary>The keyed forms render, in English, exactly the stored English; in German, German.</summary>
    [Fact]
    public void TheKeyedOutcome_ReproducesItsEnglish_AndReadsGermanForAGermanViewer()
    {
        var ended = EndedStalled();
        ended.FailureText!.Reproduces(ended.Failure).Should().BeTrue();
        var german = RebootText.Localize(ended.Failure, ended.FailureText, "de")!;
        german.Should().StartWith("festgefahren: Status AwaitingRestart, kein Fortschritt seit 2026-01-01 00:00:00Z");
        german.Should().Contain("kein danach gestarteter Prozess hat seine Prüfungen gemeldet");
        RebootText.Localize(ended.Failure, ended.FailureText, "en").Should().Be(ended.Failure, "control: an English viewer reads the stored text");

        var log = InstanceReboot.LocalizedLog(ended, "de");
        log[^1].Should().StartWith("2026-01-01 02:00:00Z als fehlgeschlagen beendet — festgefahren: Status AwaitingRestart");

        // A writer that replaces the text without the keys is rendered verbatim — never as the stale keys.
        var rewritten = ended with { Failure = "something else" };
        RebootText.Localize(rewritten.Failure, rewritten.FailureText, "de").Should().Be("something else");
    }

    /// <summary>
    /// The executor's own compositions keep their English exactly (the wire value every reader and
    /// log uses), and none renders a raw key — a key missing from the catalog would print its name.
    /// </summary>
    [Fact]
    public void TheExecutorSentences_KeepTheirEnglish()
    {
        InstanceRebootExecutor.DescribeAdopt(new ModuleAdoptOutcome { Failure = "registry unreachable" })
            .Should().Be("not landed — registry unreachable");
        InstanceRebootExecutor.DescribeAdopt(new ModuleAdoptOutcome { FilesLanded = 3, LandedVersion = "1.2.0" })
            .Should().Be("landed 1.2.0 (3 file(s))");
        InstanceRebootExecutor.DescribeAdopt(new ModuleAdoptOutcome())
            .Should().Be("not landed — no decision: no reason recorded (served none; nothing keeps running)");
        LocalizationCatalogText(InstanceRebootExecutor.DescribeAdoptText(new ModuleAdoptOutcome()), "de")
            .Should().Be("nicht gelandet — keine Entscheidung: kein Grund festgehalten (angeboten: keine; nichts läuft weiter)");

        var request = AwaitingRestart() with
        {
            Replicas = ImmutableDictionary<string, InstanceRebootReplica>.Empty
                .Add("p1", new InstanceRebootReplica
                {
                    Process = "p1", StartedAt = Restart.AddMinutes(1), ReportedAt = Restart.AddMinutes(2), Image = "9.9.9",
                    Checks =
                    [
                        new InstanceRebootCheck { Name = "smoke:thread", Outcome = InstanceRebootCheckOutcome.Passed, Detail = "ok" },
                        InstanceReboot.CheckOf("health:x", InstanceRebootCheckOutcome.NotMeasured,
                            RebootText.Of("reboot.check.notRegistered", ("component", "X"))),
                    ],
                }),
        };
        var verdict = InstanceReboot.EvaluateVerification(request, _ => true)!;
        verdict.Passed.Should().BeTrue();
        verdict.Detail.Should().Be("1 replica(s) booted after the restart verified green: p1 on 9.9.9 (1 check(s) passed) "
                                   + "[p1: health:x not measured (X is not registered on this host)]");
        LocalizationCatalogText(verdict.DetailText!, "de").Should().Be(
            "1 nach dem Neustart gestartete Replika(s) grün verifiziert: p1 auf 9.9.9 (1 Prüfung(en) bestanden) "
            + "[p1: health:x nicht gemessen (X ist auf diesem Host nicht registriert)]");

        var bake = NodeTypeBakeRebootCheck.Judge("health:bake", BakePhase.Complete, "",
            [new("Acme/A", "regressed"), new("Other/B", "broken")], ["Acme"]);
        bake.Detail.Should().Be("1 critical NodeType(s) failed the bake (phase Complete): Acme/A (regressed); "
                                + "1 non-critical type(s) also failing: Other/B");
        bake.DetailText!.Reproduces(bake.Detail).Should().BeTrue();

        foreach (var text in new[] { verdict.DetailText!, bake.DetailText!, EndedStalled().FailureText! })
            text.ToEnglish().Should().NotContain("reboot.", "every key the sentence names exists in the catalog");
    }

    /// <summary>
    /// A request written before the keys existed keeps its English lines verbatim; the lines written
    /// after read German — the keyed log is aligned with the English from its END, through trimming.
    /// </summary>
    [Fact]
    public void ALegacyLog_StaysVerbatim_AndNewLinesAreLocalized()
    {
        var legacy = AwaitingRestart() with { Log = ["old 1", "old 2", "old 3"] };
        var appended = InstanceReboot.AppendLog(legacy, 3,
            RebootText.Verbatim("2026-01-01 00:00:00Z").Then(RebootText.Of("reboot.restart.requested")));
        appended.Log.Should().Equal("old 2", "old 3", "2026-01-01 00:00:00Z requested");
        InstanceReboot.LocalizedLog(appended, "de").Should().Equal("old 2", "old 3", "2026-01-01 00:00:00Z angefordert");
    }

    /// <summary>
    /// End to end, the way a person opens it: the request's page as a GERMAN viewer renders the
    /// failure, the stalled step and the log line in German; an ENGLISH viewer reads the stored text.
    /// Before the page existed the request rendered through the generic content view — English for
    /// every viewer.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRequestPage_RendersTheOutcomeInTheViewersLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"loc-{Guid.NewGuid():N}"[..20];
        var path = $"{InstanceRebootRequest.Namespace}/{id}";
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode(id, InstanceRebootRequest.Namespace)
            {
                Name = "Reboot",
                NodeType = InstanceRebootRequest.NodeType,
                Content = EndedStalled(),
            }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var german = await PageStrings(path, "de")
            .Should().Within(TestTimeouts.Convergence)
            .Match(s => s.Any(t => t.StartsWith("festgefahren: Status AwaitingRestart", StringComparison.Ordinal)), "the German failure", ct);
        german.Should().Contain("Instanz-Neustart — Fehlgeschlagen");
        german.Should().Contain(t => t.Contains("als fehlgeschlagen beendet — festgefahren:", StringComparison.Ordinal), "the log line is German");
        german.Should().NotContain(t => t.StartsWith("stalled: ", StringComparison.Ordinal), "a German viewer never reads the English failure");

        var english = await PageStrings(path, "en")
            .Should().Within(TestTimeouts.Convergence)
            .Match(s => s.Any(t => t.StartsWith("stalled: status AwaitingRestart", StringComparison.Ordinal)), "the English failure", ct);
        english.Should().Contain("Instance reboot — Failed");
    }

    private IObservable<IReadOnlyList<string>> PageStrings(string path, string locale)
    {
        TestUsers.DevLogin(Mesh, TestUsers.Admin with { Locale = locale });
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(path), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .Select(change => (IReadOnlyList<string>)Strings(change.Value).ToList());
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString() ?? ""],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };

    private static string LocalizationCatalogText(RebootText text, string locale) => text.Render(locale);
}
