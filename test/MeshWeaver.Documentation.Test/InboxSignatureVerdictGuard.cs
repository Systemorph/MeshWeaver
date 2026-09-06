using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The escalation of #3338, held to the one thing that makes an escalation worth having: it must
/// FIRE on the misconfiguration and STAY SILENT on the legitimate case. Both publishing lanes read
/// the webhook inbox's answer and decide whether the release event / publication record was stored
/// HAVING BEEN VERIFIED — <c>main-cd.yml</c> ("Did the inbox VERIFY the build fact?") and the
/// reusable <c>node-repo-publish-bake.yml</c> ("Did the inbox VERIFY the publication record?").
///
/// <para>🚨 <b>Why this guard EXECUTES the step instead of reading it.</b> Every earlier control on
/// this leg was a substring assertion, and the defect it missed was invisible to one: the step
/// tested <c>grep -q '"signature"…"verified"'</c> and reported EVERYTHING ELSE as
/// "the instance declares no SecretConfigKey", so the ABSENCE of a verdict was reported as a
/// verdict. Measured on 2026-09-06 (core CD run 34039122957): the control instance answered
/// <c>200</c> with an EMPTY body on both lanes — it still runs the pre-#3312 endpoint
/// (<c>Results.Ok()</c>) — and both lanes named a chart key the pod could not have read. A
/// substring test cannot tell "classifies three states" from "classifies two and guesses"; running
/// the script can. So the script itself is lifted out of the YAML verbatim and run under
/// <c>bash</c> against synthetic answers, which is why both steps read their response file through
/// <c>RESP="${RESP:-/tmp/resp}"</c>.</para>
///
/// <para>🚨 <b>The discrimination, stated once.</b> A sender that signs expects to be verified;
/// a sender that does not sign has no expectation. BOTH lanes always sign — <c>main-cd</c>'s
/// <c>preflight</c> asserts <c>PLATFORM_WEBHOOK_SECRET</c> and the satellite lane's POST step fails
/// RED without <c>webhook-secret</c>, and both always send <c>X-Hub-Signature-256</c> — so within
/// these lanes <c>not-required</c> is never legitimate: it is the receiver saying, in as many
/// words, that the signature we did send was never looked at. The legitimate <c>not-required</c>
/// case is a target with an UNSIGNED sender (Stripe on <c>Store/Payments</c>, GitHub on its own
/// target), which neither of these steps ever runs against. There is no flag, no opt-out and no
/// "verification expected" input: the expectation IS the act of signing, so a target cannot be
/// moved into the unsigned category without removing the lane's secret — at which point the POST
/// step fails first, naming what to provision.</para>
///
/// <para>🚨 <b>Why the absent verdict is NOT fatal.</b> It is not the receiver declining to verify;
/// it is a receiver that cannot answer the question because it has not rolled #3312's endpoint.
/// Nothing in this repository can fix that, and failing on it would red every promoted build until
/// a roll this repo does not control happens — the outage #3338 was split out of #3312 to avoid.
/// The escalation therefore arms ITSELF: the moment a receiver answers a verdict at all, the
/// <c>not-required</c> branch becomes reachable and fatal, with nothing for anyone to remember.
/// </para>
/// </summary>
public class InboxSignatureVerdictGuard
{
    private const string CoreLane = ".github/workflows/main-cd.yml";
    private const string CoreStep = "Did the inbox VERIFY the build fact?";
    private const string SatelliteLane = ".github/workflows/node-repo-publish-bake.yml";
    private const string SatelliteStep = "Did the inbox VERIFY the publication record?";

    /// <summary>The exact answer a #3312 receiver gives when it checked our HMAC.</summary>
    private const string Verified = """{"status":"accepted","signature":"verified"}""";

    /// <summary>The exact answer a #3312 receiver gives when the target declares no key.</summary>
    private const string NotRequired = """{"status":"accepted","signature":"not-required"}""";

    /// <summary>What the control instance actually answered on 2026-09-06 — a bare 200, no body.
    /// The state the old two-way test reported as a declaration.</summary>
    private const string NoBody = "";

    /// <summary>A well-formed answer that simply carries no verdict — the same state as
    /// <see cref="NoBody"/>, reached a different way.</summary>
    private const string NoVerdictField = """{"status":"accepted"}""";

    /// <summary>A verdict word neither end knows. Fail-closed: an unknown word is not a pass.</summary>
    private const string UnknownVerdict = """{"status":"accepted","signature":"maybe"}""";

    // ── the escalation FIRES ─────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE CASE THE ESCALATION IS FOR. The receiver ran the verdict code and told us it never
    /// looked at the signature we sent. Both lanes must END, and the message must name the ONE key
    /// that fixes it — a red whose message does not say what to provision teaches people to ignore
    /// the red.
    /// </summary>
    [Theory]
    [InlineData(CoreLane, CoreStep)]
    [InlineData(SatelliteLane, SatelliteStep)]
    public void AnAcceptedButUNVERIFIEDDelivery_FailsTheLane(string lane, string step)
    {
        var (exit, output) = RunVerdictStep(lane, step, NotRequired);

        Assert.True(exit == 1,
            $"{lane}: an inbox answering \"not-required\" to a SIGNED delivery must fail the job "
            + $"(#3338) — it says our secret was never exercised, so a drifted one is invisible. "
            + $"Exit was {exit}. Output:\n{output}");
        Assert.Contains("::error", output, StringComparison.Ordinal);
        Assert.Contains("SecretConfigKey", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verdict word neither end knows is not a pass. The whole step judges the RECEIVER's own
    /// word, so the day that word changes shape the lane must say so rather than fall through to
    /// the quiet branch — which is how "we verify now" would degrade to "we used to" a third time.
    /// </summary>
    [Theory]
    [InlineData(CoreLane, CoreStep)]
    [InlineData(SatelliteLane, SatelliteStep)]
    public void AnUnrecognisedVerdict_FailsTheLane(string lane, string step)
    {
        var (exit, output) = RunVerdictStep(lane, step, UnknownVerdict);

        Assert.True(exit == 1, $"{lane}: an unknown verdict must not read as a pass. Output:\n{output}");
        Assert.Contains("::error", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// No response file at all means the POST step did not run to completion — the one state in
    /// which this step has nothing to judge and must not decorate the job green.
    /// </summary>
    [Theory]
    [InlineData(CoreLane, CoreStep)]
    [InlineData(SatelliteLane, SatelliteStep)]
    public void NoCapturedResponse_FailsTheLane(string lane, string step)
    {
        var (exit, output) = RunVerdictStep(lane, step, responseBody: null);

        Assert.True(exit == 1, $"{lane}: a missing response file must fail. Output:\n{output}");
        Assert.Contains("::error", output, StringComparison.Ordinal);
    }

    // ── the escalation STAYS SILENT ──────────────────────────────────────────

    /// <summary>
    /// The verified path is the point of the whole leg: it must pass, silently. A step that also
    /// warned here would train the reader to ignore its warnings.
    /// </summary>
    [Theory]
    [InlineData(CoreLane, CoreStep)]
    [InlineData(SatelliteLane, SatelliteStep)]
    public void AVerifiedDelivery_PassesQuietly(string lane, string step)
    {
        var (exit, output) = RunVerdictStep(lane, step, Verified);

        Assert.True(exit == 0, $"{lane}: a verified delivery must pass. Output:\n{output}");
        Assert.DoesNotContain("::error", output, StringComparison.Ordinal);
        Assert.DoesNotContain("::warning", output, StringComparison.Ordinal);
        Assert.Contains("VERIFIED", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE ARM THAT MATTERS MOST, because getting it wrong is worse than having no escalation:
    /// a receiver that answers NO verdict has not declined to verify — it cannot answer at all. It
    /// must NOT fail the lane, and it must NOT be described as a missing declaration. That exact
    /// conflation is what both lanes printed on every publish until #3338: the reader was sent to
    /// provision a chart key on a pod running an endpoint that predates the key.
    /// </summary>
    [Theory]
    [InlineData(CoreLane, CoreStep, NoBody)]
    [InlineData(CoreLane, CoreStep, NoVerdictField)]
    [InlineData(SatelliteLane, SatelliteStep, NoBody)]
    [InlineData(SatelliteLane, SatelliteStep, NoVerdictField)]
    public void AnAnswerCarryingNoVerdict_DoesNotFail_AndIsNotCalledAMissingDeclaration(
        string lane, string step, string responseBody)
    {
        var (exit, output) = RunVerdictStep(lane, step, responseBody);

        Assert.True(exit == 0,
            $"{lane}: an answer with no signature verdict is a receiver that has not rolled #3312, "
            + $"not a target that declines to verify. Failing it reds every publish for a roll no "
            + $"repository here controls — the outage #3338 exists to avoid. Exit was {exit}. "
            + $"Output:\n{output}");
        Assert.DoesNotContain("::error", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretConfigKey", output, StringComparison.Ordinal);
        Assert.Contains("ROLLOUT", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two lanes POST the same shape to the same endpoint and must never disagree about what
    /// its answer means — a satellite going red where core goes green (or the reverse) is a state
    /// nobody could act on. They are two inline scripts rather than one shared file because the
    /// satellite job runs in the CALLING repository's checkout, where core's <c>.github/scripts</c>
    /// is not present; this is what stops the copies drifting.
    /// </summary>
    [Fact]
    public void BothLanesClassifyEveryAnswerIdentically()
    {
        var answers = new (string Name, string? Body)[]
        {
            ("verified", Verified),
            ("not-required", NotRequired),
            ("no body", NoBody),
            ("no verdict field", NoVerdictField),
            ("unknown verdict", UnknownVerdict),
            ("no response captured", null),
        };

        var disagreements = answers
            .Select(a => (
                a.Name,
                Core: RunVerdictStep(CoreLane, CoreStep, a.Body).Exit,
                Satellite: RunVerdictStep(SatelliteLane, SatelliteStep, a.Body).Exit))
            .Where(r => r.Core != r.Satellite)
            .Select(r => $"  {r.Name}: main-cd exits {r.Core}, node-repo-publish-bake exits {r.Satellite}")
            .ToArray();

        Assert.True(disagreements.Length == 0,
            "the two publishing lanes read the same inbox answer differently:\n"
            + string.Join("\n", disagreements));
    }

    // ── the shape, so the classification cannot regress to a substring test ──

    /// <summary>
    /// The step must SWITCH on the receiver's word, not test for one string. The regressed form is
    /// specifically <c>grep …"verified"</c> plus an <c>else</c>: it cannot represent the third
    /// state, so it necessarily mislabels it. Asserted textually as well as behaviourally because
    /// a future rewrite could reproduce the three exit codes with two branches and a comment.
    /// </summary>
    [Theory]
    [InlineData(CoreLane, CoreStep)]
    [InlineData(SatelliteLane, SatelliteStep)]
    public void TheStepSwitchesOnTheVerdict_RatherThanTestingForOneString(string lane, string step)
    {
        var script = VerdictScript(lane, step);

        Assert.Contains("case \"$verdict\" in", script, StringComparison.Ordinal);
        Assert.Contains("not-required)", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", script, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -Eq '\"signature\"", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The satellite lane's job forbids <c>::warning</c> anywhere in it
    /// (<c>UpstreamBuildGateGuard.TheLaneEndsByRegisteringWithMemex_AndDispatchesToNobody</c>,
    /// because a delivery failure was once downgraded to one). So its quiet branch is a plain echo
    /// and its loud branch is an <c>::error::</c> — never a warning in between. Pinned here so the
    /// three-way classification cannot be "harmonised" with core's by adding one.
    /// </summary>
    [Fact]
    public void TheSatelliteLaneNeverWarns()
    {
        Assert.DoesNotContain("::warning", VerdictScript(SatelliteLane, SatelliteStep),
            StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The named step's <c>run: |</c> body, lifted verbatim out of the workflow and dedented — the
    /// bytes CI executes, not a restatement of them.
    /// </summary>
    private static string VerdictScript(string workflow, string stepName)
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(),
            workflow.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{workflow} is gone — the publishing lane it holds has moved.");
        var lines = File.ReadAllLines(path);

        var stepAt = Array.FindIndex(lines, l => l.Contains($"- name: \"{stepName}\"", StringComparison.Ordinal));
        Assert.True(stepAt >= 0,
            $"step '{stepName}' is gone from {workflow} — the signature verdict is no longer judged, "
            + "which no other check would catch.");

        var runAt = Array.FindIndex(lines, stepAt, l => l.TrimEnd().EndsWith("run: |", StringComparison.Ordinal));
        Assert.True(runAt > stepAt, $"step '{stepName}' in {workflow} no longer carries a `run: |` script.");
        var runIndent = Indent(lines[runAt]);

        var body = new List<string>();
        for (var i = runAt + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                body.Add("");
                continue;
            }
            if (Indent(lines[i]) <= runIndent)
                break;
            body.Add(lines[i]);
        }
        Assert.True(body.Count > 0, $"step '{stepName}' in {workflow} has an empty script.");

        var dedent = body.Where(l => l.Length > 0).Min(Indent);
        return string.Join("\n", body.Select(l => l.Length == 0 ? l : l[dedent..])) + "\n";
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /// <summary>
    /// Runs the extracted step against one synthetic inbox answer. <paramref name="responseBody"/>
    /// null means the POST step captured nothing at all.
    /// </summary>
    private static (int Exit, string Output) RunVerdictStep(
        string workflow, string stepName, string? responseBody)
    {
        var script = VerdictScript(workflow, stepName);
        var dir = Path.Combine(Path.GetTempPath(), "mw-3338-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, "step.sh");
            File.WriteAllText(scriptPath, script);
            var responsePath = Path.Combine(dir, "resp");
            if (responseBody is not null)
                File.WriteAllText(responsePath, responseBody);

            var psi = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.Environment["RESP"] = responsePath;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    "bash could not be started. It is the shell CI runs these steps in, so this is a "
                    + "real missing prerequisite, not a reason to skip the control.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
