using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for CD's release-event leg (<c>#2235</c>) — the ONE thing that turns a promoted
/// platform build into a cross-repo rebake wave. CD signs a POST to the control instance's webhook
/// inbox and FINISHES; the mesh verifies the HMAC, bumps every node repo's <c>MW_IMAGE_DIGEST</c>
/// pin and broadcasts <c>meshweaver-framework-released</c> to each subscriber
/// (<c>FrameworkReleaseBroadcaster</c>, <c>src/MeshWeaver.GitSync</c>, called by the Hosting
/// module's <c>PlatformBuildInboxWatcher</c>). Core itself dispatches to no repository — see
/// <see cref="CoreDispatchesToNoRepository"/>.
///
/// <para>🚨 <b>Why a guard and not a test:</b> the leg cannot be exercised from this repository —
/// its counterparty is a running mesh — so the ONLY thing that can fail when it regresses is a
/// reading of the workflow text. And it HAS regressed, in the exact shape this file forbids: the
/// job opened with <c>if [ -z "$URL" ] || [ -z "$SECRET" ]; then … exit 0</c> and downgraded every
/// non-2xx to a <c>::warning::</c>, so an unprovisioned or refusing delivery path produced a green
/// tick on every promote. Zero releases were broadcast between 2026-08-22 and 2026-08-25 and CD
/// reported success each time.</para>
///
/// <para>Each assertion below names the failure it would let through. None of them asserts that the
/// wave RAN — that is unobservable from here, and pretending otherwise would be the same lie one
/// level up (see <see cref="A2xxIsNotClaimedAsProofTheWaveRan"/>).</para>
/// </summary>
public class PlatformReleaseNotifyGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";

    /// <summary>The workflow's steps, comments stripped — a guard must judge what RUNS, never the
    /// prose explaining it.</summary>
    private static string Body() => string.Join("\n",
        File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow))
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#')));

    /// <summary>The workflow verbatim, comments included — for the few assertions that are ABOUT
    /// the prose (a rationale that must stay attached to the code it explains).</summary>
    private static string Verbatim() => File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow));

    /// <summary>
    /// 🚨 A gate NEVER tests its own inputs. `preflight` must assert both halves of the release
    /// event's credential — the inbox URL and the shared HMAC — and fail RED naming them. Without
    /// it, the notify job is free to grow back the `-z "$SECRET"` escape hatch, which GitHub
    /// renders identically to a delivery that succeeded.
    /// </summary>
    [Fact]
    public void PreflightAssertsBothHalvesOfTheReleaseEventCredential()
    {
        var preflight = JobBlock(Body(), "preflight:");

        Assert.Contains("PLATFORM_WEBHOOK_URL", preflight, StringComparison.Ordinal);
        Assert.Contains("PLATFORM_WEBHOOK_SECRET", preflight, StringComparison.Ordinal);
        Assert.Contains("exit 1", preflight, StringComparison.Ordinal);
        Assert.Contains("::error", preflight, StringComparison.Ordinal);

        Assert.DoesNotContain("continue-on-error", preflight, StringComparison.Ordinal);

        // 🚨 It asserts; it does not decide whether to run — with ONE exemption, and the exemption
        // has a shape. A condition on the EVENT (a fork's PR run, which GitHub withholds org
        // secrets from by design) is legitimate and is the only reason this job may be skipped. A
        // condition asking whether the input EXISTS is the trapdoor one level up: it makes the
        // assertion itself skippable, and on the checks page the two render identically.
        var condition = SectionAfter(preflight, "\n    if:");
        Assert.DoesNotContain("secrets.", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("vars.", condition, StringComparison.Ordinal);
        Assert.Contains("github.event", condition, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The notify job must DEPEND on preflight and must not re-check the inputs itself. The two
    /// shapes are indistinguishable on the checks page and only one of them can fail.
    /// </summary>
    [Fact]
    public void NotifyDependsOnPreflight_AndCarriesNoInputShapedCondition()
    {
        var notify = JobBlock(Body(), "notify-platform-update:");

        Assert.Contains("needs: [preflight", notify, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", notify, StringComparison.Ordinal);

        // The one `if:` allowed is about the RUN (did we publish?), never about an input.
        Assert.DoesNotContain("-z \"${URL", notify, StringComparison.Ordinal);
        Assert.DoesNotContain("-z \"${SECRET", notify, StringComparison.Ordinal);
        Assert.DoesNotContain("-z \"$URL", notify, StringComparison.Ordinal);
        Assert.DoesNotContain("-z \"$SECRET", notify, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT CONFIGURED", notify, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE REGRESSION THIS FILE EXISTS FOR. A release event the mesh never received is a rebake
    /// wave that did not happen — it must end the job, not decorate it. `exit 0` anywhere in the
    /// POST step, or a `::warning::` on the failure branch, puts the eight silent days straight
    /// back.
    /// </summary>
    [Fact]
    public void ARefusedOrUndeliveredReleaseEventFailsTheJob()
    {
        var post = StepBlock(Body(), "Sign and POST the build fact");

        Assert.DoesNotContain("exit 0", post, StringComparison.Ordinal);
        Assert.DoesNotContain("::warning", post, StringComparison.Ordinal);

        // EVERY ::error:: in this step terminates the job — counted rather than spot-checked,
        // because the way this regresses is someone adding a fourth branch that only prints.
        Assert.Contains("::error", post, StringComparison.Ordinal);
        Assert.True(CountOf(post, "::error") >= 4,
            "the POST step must distinguish unreachable / wrong-URL / refused / no-digest — one "
            + "shared message sends the reader to the wrong fix.");
        Assert.Equal(CountOf(post, "::error"), CountOf(post, "exit 1"));

        // A curl that dies at the TRANSPORT level must reach the verdict rather than killing the
        // step under `set -e` with a bare curl error — the `|| code=000` branch is that guarantee.
        Assert.Contains("code=000", post, StringComparison.Ordinal);
        Assert.Contains("--max-time", post, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The half this repository CANNOT see, written down where the next reader will look. The
    /// inbox is deliberately dumb, so a 2xx means "stored", not "the wave ran": if the control
    /// instance's <c>Hosting:PlatformWebhookSecret</c> is unset or differs from CD's, the watcher
    /// drops every delivery as unverifiable and this job still goes green. A summary line claiming
    /// the release was "notified to the update agent" is precisely the sentence that made eight
    /// days of zero dispatches look like success.
    /// </summary>
    [Fact]
    public void A2xxIsNotClaimedAsProofTheWaveRan()
    {
        var post = StepBlock(Body(), "Sign and POST the build fact");
        Assert.DoesNotContain("notified to the update agent", post, StringComparison.Ordinal);
        Assert.Contains("accepted by the inbox", post, StringComparison.Ordinal);

        Assert.Contains("Hosting:PlatformWebhookSecret", Verbatim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 A failure nobody is paged for is still invisible. `alert-on-failure` is what files the
    /// `ci-failure` issue, so both the input assertion and the delivery leg must be in its
    /// `needs` — otherwise a red notify job is a red tick on a page nobody opens.
    /// </summary>
    [Fact]
    public void AFailedReleaseEventIsAlerted()
    {
        var alert = JobBlock(Body(), "alert-on-failure:");
        var needs = alert.Split('\n').First(l => l.TrimStart().StartsWith("needs:", StringComparison.Ordinal));

        Assert.Contains("preflight", needs, StringComparison.Ordinal);
        Assert.Contains("notify-platform-update", needs, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 CORE DISPATCHES TO NO REPOSITORY. Maintainer, 2026-09-03: "None of the top-level repos
    /// should have any dependency to anyone else. It must be event based: (1) memex issues an event
    /// that something has a new version; (2) GitHub subscribes to this and triggers the build. Core
    /// publishes an event and finishes." The one event core emits is the signed build fact
    /// <c>notify-platform-update</c> POSTs into the control instance's inbox; the fan-out
    /// (<c>meshweaver-framework-released</c>) is memex's, and the subscriber set is the
    /// <c>Hosting/Deployment</c> records' registry sources — data in the mesh, not a list here.
    ///
    /// <para>What this refuses is any `repository_dispatch` SENDER in a workflow core runs on its own
    /// behalf — a `/repos/…/dispatches` POST, a dispatch action, an `event_type` payload. Two such
    /// jobs have lived in this repo (2026-08-22 and 2026-08-29 → 2026-09-03), each justified as "a
    /// second, independent path to the same event" while the memex hop was silent — and the hop
    /// was silent because the broadcaster had NO CALLER, not because a mesh hop is unreliable. Two
    /// emitters for one event is the cross-repo coupling the rule forbids.</para>
    ///
    /// <para>The ledger below is the ONE exemption class and it is judged, not skipped: a reusable
    /// <c>workflow_call</c> lane runs in the CALLING satellite's context, so when
    /// <c>node-repo-publish-bake.yml</c> sends <c>meshweaver-upstream-published</c> and
    /// <c>node-repo-tag-modules.yml</c> sends <c>meshweaver-modules-published</c>, the SATELLITE is
    /// the sender telling its own dependents — core merely hosts the shared lane and never runs it on
    /// its own behalf. A ledgered file that stops sending fails too: a guard whose subject moved and
    /// whose roots did not passes having checked nothing.</para>
    /// </summary>
    [Fact]
    public void CoreDispatchesToNoRepository()
    {
        var workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        // Every ledgered sender must be a reusable lane (`on: workflow_call`) — the property that
        // makes the caller, not core, the sender. Asserted, not assumed.
        var ledger = new[] { "node-repo-publish-bake.yml", "node-repo-tag-modules.yml" };
        foreach (var name in ledger)
        {
            var text = File.ReadAllText(Path.Combine(workflows, name));
            Assert.True(text.Contains("workflow_call:", StringComparison.Ordinal)
                        && !text.Contains("\n  push:", StringComparison.Ordinal)
                        && !text.Contains("\n  workflow_run:", StringComparison.Ordinal),
                $"{name} is ledgered as a satellite-context sender, but it no longer runs only as a "
                + "reusable workflow_call lane — a dispatch it sends on a push or workflow_run would be core's.");
        }

        // The withdrawn leg, by name — the shape most likely to be re-added from memory.
        Assert.False(File.Exists(Path.Combine(workflows, "notify-dependents.yml")),
            "notify-dependents.yml is back: core must not push the release wave itself — memex does, "
            + "from the build fact core POSTs (main-cd.yml, 'THE RELEASE WAVE').");

        var senders = Directory.EnumerateFiles(workflows, "*.yml")
            .Select(f => (Name: Path.GetFileName(f), Sends: SendsARepositoryDispatch(f)))
            .Where(x => x.Sends)
            .Select(x => x.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var unexpected = senders.Except(ledger, StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Length == 0,
            "these workflows send a repository_dispatch to another repository:\n  "
            + string.Join("\n  ", unexpected)
            + "\nCore publishes ONE event (the build fact POSTed to Hosting/PlatformBuilds) and finishes; "
            + "the fan-out is memex's (FrameworkReleaseBroadcaster, called by the Hosting module's "
            + "PlatformBuildInboxWatcher). Remove the dispatch — do not extend the ledger.");

        var vanished = ledger.Except(senders, StringComparer.Ordinal).ToArray();
        Assert.True(vanished.Length == 0,
            "ledgered sender(s) no longer send a repository_dispatch — the ledger is stale, or the "
            + "satellite→dependent wake moved somewhere this guard does not read:\n  "
            + string.Join("\n  ", vanished));

        // The main CD workflow, specifically: no job key of either historical shape, no reusable call.
        var body = Body();
        Assert.DoesNotContain("\n  notify-dependents:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  dispatch-dependents:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("uses: ./.github/workflows/notify-dependents.yml", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BAKE_SUBSCRIBER_REPOS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DEPENDENT_DISPATCH_TOKEN", body, StringComparison.Ordinal);
        var legs = SectionAfter(JobBlock(body, "delivery-verdict:"), "LEGS: >-");
        Assert.DoesNotContain("notify-dependents=", legs, StringComparison.Ordinal);
        Assert.Contains("notify-platform-update=", legs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The detector must FIRE on each way a workflow can send a repository_dispatch and stay silent
    /// on prose about one — otherwise the guard above is either a trapdoor or a nuisance.
    /// </summary>
    [Fact]
    public void TheDispatchDetector_FiresOnEachSendingForm_AndIsSilentOnProse()
    {
        Assert.True(Sends("  run: gh api -X POST /repos/$repo/dispatches --input -"), "gh api POST …/dispatches");
        Assert.True(Sends("  run: curl -X POST https://api.github.com/repos/Systemorph/X/dispatches"), "curl …/dispatches");
        Assert.True(Sends("  uses: peter-evans/repository-dispatch@v3"), "the dispatch action");
        Assert.True(Sends("  script: github.rest.repos.createDispatchEvent({owner, repo, event_type})"), "github-script");
        Assert.True(Sends("  run: jq -cn '{event_type:\"meshweaver-framework-released\", client_payload:$p}'"), "an event_type payload");

        Assert.False(Sends("  # memex fans a repository_dispatch out to every subscriber (/dispatches)"), "a comment");
        Assert.False(Sends("  # 🚨 History: a `notify-dependents` job lived here twice"), "history prose");
        Assert.False(Sends("on:\n  repository_dispatch:\n    types: [meshweaver-framework-released]"), "a RECEIVER is not a sender");
        Assert.False(Sends("  run: echo \"woken by ${{ github.event.action }}\""), "reading the event");
    }

    private static bool Sends(string yaml) => SendsARepositoryDispatch(yaml.Split('\n'));

    private static bool SendsARepositoryDispatch(string path) =>
        SendsARepositoryDispatch(File.ReadAllLines(path));

    /// <summary>A workflow SENDS a dispatch when a non-comment line POSTs to a `/dispatches` endpoint,
    /// uses a dispatch action, calls `createDispatchEvent`, or builds an `event_type` payload. A
    /// `repository_dispatch:` trigger is a receiver and does not count.</summary>
    private static bool SendsARepositoryDispatch(string[] lines) =>
        lines.Where(l => !l.TrimStart().StartsWith('#')).Any(l =>
            l.Contains("/dispatches", StringComparison.Ordinal)
            || l.Contains("repository-dispatch@", StringComparison.Ordinal)
            || l.Contains("createDispatchEvent", StringComparison.Ordinal)
            || l.Contains("event_type", StringComparison.Ordinal));

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Everything from <paramref name="marker"/> to the next key at the same indent — for
    /// judging a multi-line YAML value in isolation.</summary>
    private static string SectionAfter(string body, string marker)
    {
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{marker.Trim()}' is gone from the preflight job in {Workflow}.");
        var next = NextAtIndent(body, start + marker.Length, "\n    ");
        return next < 0 ? body[start..] : body[start..next];
    }

    /// <summary>Everything from a job's key to the next job key at the same indent.</summary>
    private static string JobBlock(string body, string jobKey)
    {
        var start = body.IndexOf("\n  " + jobKey, StringComparison.Ordinal);
        Assert.True(start >= 0, $"job '{jobKey}' is gone from {Workflow} — it has been removed or "
                                + "renamed, which no other check would catch.");
        var next = NextAtIndent(body, start + jobKey.Length + 3, "\n  ");
        return next < 0 ? body[start..] : body[start..next];
    }

    /// <summary>Everything from a step's name to the next step boundary at the same indent, so an
    /// assertion about one step cannot be satisfied by another.</summary>
    private static string StepBlock(string body, string stepName)
    {
        var start = body.IndexOf(stepName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"step '{stepName}' is gone from {Workflow} — the delivery leg it "
                                + "belongs to has been removed or renamed.");
        var next = body.IndexOf("\n      - name:", start + stepName.Length, StringComparison.Ordinal);
        return next < 0 ? body[start..] : body[start..next];
    }

    /// <summary>The next line whose indent is exactly <paramref name="prefix"/> followed by a
    /// non-space — i.e. the next sibling key, skipping everything nested under this one.</summary>
    private static int NextAtIndent(string body, int from, string prefix)
    {
        var i = from;
        while (true)
        {
            i = body.IndexOf(prefix, i, StringComparison.Ordinal);
            if (i < 0)
                return -1;
            var c = i + prefix.Length < body.Length ? body[i + prefix.Length] : '\0';
            if (c is not (' ' or '\n' or '#' or '\0'))
                return i;
            i += prefix.Length;
        }
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
