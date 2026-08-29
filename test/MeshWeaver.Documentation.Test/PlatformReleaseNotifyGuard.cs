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
    /// 🚨 The dependents ARE dispatched from here (maintainer, 2026-08-29: "it should be github actions
    /// trigger — no in mesh baking — on platform update ⇒ update all"), and the way it is done is
    /// what this guard pins, because both previous designs failed in the same silent shape:
    /// <c>BAKE_SUBSCRIBER_REPOS</c> + a stored PAT printed "NOT CONFIGURED" for its whole life, and
    /// the mesh broadcast that replaced it dispatched to an empty subscriber set on every deploy
    /// (#2235). So: no hand-maintained list (the set is the dispatch App's installation, read at
    /// run time), no stored token (an App installation token minted per run), the credential asserted
    /// RED in preflight, and the job itself a delivery leg the verdict requires — never a skip.
    /// </summary>
    [Fact]
    public void TheDispatchIsDiscoveredAppCredentialedAndAssertedRed()
    {
        var body = Body();
        Assert.DoesNotContain("BAKE_SUBSCRIBER_REPOS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DEPENDENT_DISPATCH_TOKEN", body, StringComparison.Ordinal);

        // ONE fan-out. Two sessions built this leg twice on 2026-08-29 (an inline job and the
        // reusable notify-dependents.yml); the reusable then fired BEFORE the Plugins bake with a
        // payload the receivers could not use (run 33276813173). A second job key here is the
        // duplication coming back.
        Assert.DoesNotContain("\n  dispatch-dependents:", body, StringComparison.Ordinal);

        var job = JobBlock(body, "notify-dependents:");
        Assert.Contains("uses: ./.github/workflows/notify-dependents.yml", job, StringComparison.Ordinal);
        Assert.Contains("secrets.DEPENDENT_DISPATCH_APP_ID", job, StringComparison.Ordinal);
        Assert.Contains("secrets.DEPENDENT_DISPATCH_APP_PRIVATE_KEY", job, StringComparison.Ordinal);
        // Plugins is built WITH the platform (plugins-bake), never told to rebuild — and the wave
        // fires only after that publication is sealed: a satellite woken earlier seeds nothing.
        Assert.Contains("needs.plugins-bake.result == 'success'", job, StringComparison.Ordinal);
        // The receivers read image + digest (+ sha); a version alone is not a release event.
        Assert.Contains("image: ${{ needs.plugins-bake-image.outputs.image }}", job, StringComparison.Ordinal);
        Assert.Contains("digest: ${{ needs.plugins-bake-image.outputs.digest }}", job, StringComparison.Ordinal);
        Assert.Contains("sha: ${{ needs.gate.outputs.sha }}", job, StringComparison.Ordinal);

        // The reusable itself: discovered subscribers, an App token minted per run, RED on any
        // failed dispatch (collected, then exit 1 — never continue-on-error), and a platform event
        // without its image refused rather than sent.
        var reusable = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "notify-dependents.yml"));
        Assert.Contains("actions/create-github-app-token", reusable, StringComparison.Ordinal);
        Assert.Contains("/installation/repositories", reusable, StringComparison.Ordinal);
        Assert.Contains("meshweaver-framework-released", reusable, StringComparison.Ordinal);
        Assert.Contains("client_payload:$p", reusable, StringComparison.Ordinal);
        Assert.Contains("reason=platform but inputs.image is empty", reusable, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", reusable, StringComparison.Ordinal);
        Assert.Contains("exit 1", reusable, StringComparison.Ordinal);

        var preflight = SectionAfter(JobBlock(body, "preflight:"), "missing=()");
        Assert.Contains("DEPENDENT_DISPATCH_APP_ID", preflight, StringComparison.Ordinal);
        Assert.Contains("DEPENDENT_DISPATCH_APP_PRIVATE_KEY", preflight, StringComparison.Ordinal);

        var legs = SectionAfter(JobBlock(body, "delivery-verdict:"), "LEGS: >-");
        Assert.Contains("notify-dependents=", legs, StringComparison.Ordinal);
        Assert.Contains("plugins-bake=", legs, StringComparison.Ordinal);
    }

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
