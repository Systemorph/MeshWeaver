using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for CD's release-event leg (<c>#2235</c>) — the ONE thing that turns a promoted
/// platform build into a cross-repo rebake wave. CD signs a POST to the control instance's webhook
/// inbox; the mesh verifies the HMAC, bumps every node repo's <c>MW_IMAGE_DIGEST</c> pin and
/// broadcasts <c>meshweaver-framework-released</c> to each subscriber
/// (<c>FrameworkReleaseBroadcaster</c>, <c>src/MeshWeaver.GitSync</c>).
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

        // It asserts; it does not decide whether to run. An `if:` here would make the assertion
        // itself skippable — the trapdoor one level up.
        Assert.DoesNotContain("\n    if:", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", preflight, StringComparison.Ordinal);
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
    /// 🚨 No hand-maintained subscriber list, and no satellite write credential, may creep back
    /// into the release path. The subscriber set lives in the Hosting fleet registry on the control
    /// instance; the platform holds no write access to any node repo and signs one POST instead.
    /// `BAKE_SUBSCRIBER_REPOS` was the previous attempt — it was never provisioned, printed
    /// "NOT CONFIGURED" for its whole life, and survived as a repo variable holding one stale repo
    /// name long after the design moved on.
    /// </summary>
    [Fact]
    public void TheReleasePathHoldsNoSubscriberListAndNoSatelliteCredential()
    {
        var body = Body();

        Assert.DoesNotContain("BAKE_SUBSCRIBER_REPOS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DEPENDENT_DISPATCH_TOKEN", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/dispatches", body, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
