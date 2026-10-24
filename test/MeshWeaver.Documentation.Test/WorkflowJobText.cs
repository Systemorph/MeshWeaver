#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Reads ONE job's body out of a workflow file, by indentation, for the lane guards that assert on
/// a job's text.
///
/// <para>🚨 <b>Why this exists as one implementation with a diagnosis attached.</b> Three guards
/// (<see cref="ModuleSuiteLaneGuard"/>, <see cref="ModuleBuildLedgerLaneGuard"/>,
/// <see cref="ModuleIdentityPublishGuard"/>) each carried a byte-identical private
/// <c>JobBody</c>, and all three answered the SAME question wrongly in the SAME way. A job's body
/// is matched as a run of lines that are either indented four spaces or a two-space comment, up to
/// the next two-space key. A line at ANY OTHER indentation — most easily a comment written at
/// column 0 — matches none of those and cannot be skipped, so the whole match fails and the
/// assertion that fires is <c>"… must have a `select` job"</c>.</para>
///
/// <para><b>That sentence is false, and its falsehood is the expensive part.</b> Measured on
/// MeshWeaver#4603 (2026-09-17): six continuation lines of a comment landed at column 0 inside the
/// <c>select</c> job of <c>node-repo-module-pack.yml</c>. The job was plainly there; the workflow
/// still parsed to its usual six jobs; actionlint, the duplicate-key guard and the timeout guard
/// were all green. Two shard-4 tests went red claiming the job was missing, on a pull request whose
/// subject was a <c>git fetch</c> refspec — so the first hypothesis the failure invited was a
/// shared infrastructure failure rather than six spaces.</para>
///
/// <para>So the reader now separates the two questions it was conflating — does the job KEY exist,
/// and does its body parse to the next job — and, when the second fails, names the first line it
/// could not attribute to the job, with its number and its text. A guard is allowed to refuse; it
/// is not allowed to refuse while describing a different defect.</para>
/// </summary>
internal static class WorkflowJobText
{
    /// <summary>A job body's content lines: four-space-or-deeper, or a two-space comment.</summary>
    private const string BodyLine = @"(?:    .*|  #.*)";

    /// <summary>
    /// The body of <paramref name="job"/> in <paramref name="lane"/>, comment lines removed —
    /// exactly what the three guards' own <c>JobBody</c> returned, for every input on which it
    /// returned anything at all.
    /// </summary>
    internal static string Body(string lane, string job)
    {
        var (body, refusal) = Read(File.ReadAllText(Path.Combine(FindRepoRoot(), lane)), lane, job);
        Assert.True(body is not null, refusal);
        return body!;
    }

    /// <summary>
    /// The pure half: the job's body, or the sentence explaining what stopped the read. Exactly one
    /// of the two is non-null, which is what lets <see cref="WorkflowJobTextTest"/> assert on the
    /// refusal without going through an assertion failure.
    /// </summary>
    internal static (string? Body, string? Refusal) Read(string text, string lane, string job)
    {
        var header = Regex.Match(text, @"\n  " + Regex.Escape(job) + @":\n");
        if (!header.Success)
            return (null, $"{lane} must have a `{job}` job");

        var match = Regex.Match(
            text[header.Index..],
            @"\A\n  " + Regex.Escape(job) + @":\n(?<body>(?:" + BodyLine + @"\n|\n)+?)(?=  [a-z][a-z-]*:\n|\z)");

        // The header IS there, so a failed body match means a line inside the job sits at an
        // indentation this reader cannot attribute to it. Name that line, never the job.
        if (!match.Success)
            return (null, Refusal(text, lane, job, header.Index + header.Length));

        return (
            string.Join('\n', match.Groups["body"].Value.Split('\n').Where(l => !l.TrimStart().StartsWith('#'))),
            null);
    }

    /// <summary>The first line after the job header that belongs to neither the body nor a next job.</summary>
    private static string Refusal(string text, string lane, string job, int bodyStart)
    {
        var lineNumber = text[..bodyStart].Count(c => c == '\n') + 1;
        foreach (var line in text[bodyStart..].Split('\n'))
        {
            if (line.Length == 0 || Regex.IsMatch(line, @"\A" + BodyLine + @"\z"))
            {
                lineNumber++;
                continue;
            }

            if (Regex.IsMatch(line, @"\A  [a-z][a-z-]*:\z"))
                break; // a well-formed next job — the body ended, which is not a refusal.

            return $"{lane}: the `{job}` job's body stops at line {lineNumber}, which this reader cannot "
                + "attribute to the job. A job's lines are indented four spaces or are a two-space comment, "
                + "and the next job is a two-space key. YAML accepts other indentations — a comment at "
                + "column 0 most easily — and a reader that locates a job by indentation does not. "
                + $"Indent it into the step it documents (MeshWeaver#4603).\n  {lineNumber}: {line}";
        }

        return $"{lane}: the `{job}` job's body could not be read through to the next job key.";
    }

    /// <summary>
    /// A line that opens a job: exactly two spaces, then a key of letters, digits, <c>-</c> or
    /// <c>_</c>, then <c>:</c> and nothing else but an optional trailing comment. The key is matched
    /// WHOLE — the shared answer to the prefix collision the hand-rolled slicers carried, where
    /// <c>IndexOf("\n  " + "build")</c> lands on <c>build-image:</c> when that job comes first.
    /// </summary>
    internal static bool IsJobKey(string line) => JobKeyOf(line) is not null;

    /// <summary>The key a job-opening line names, or <c>null</c> when the line opens no job.</summary>
    internal static string? JobKeyOf(string line)
    {
        var m = Regex.Match(line, @"\A  (?<key>[A-Za-z0-9_-]+):[ \t]*(?:#.*)?\z");
        return m.Success ? m.Groups["key"].Value : null;
    }

    /// <summary>
    /// Every job's <c>(Key, Start, End)</c> line range, in file order: its key line through the line
    /// before the next job key — or before the next column-0 key, which ends the <c>jobs:</c> map.
    /// The scan starts after the <c>jobs:</c> line when the text has one, so a trigger key such as
    /// <c>  push:</c> under <c>on:</c> (also a two-space key) is never taken for a job. Comment lines
    /// inside a range are KEPT: what a caller strips is its own call.
    /// </summary>
    internal static ImmutableList<(string Key, int Start, int End)> Ranges(string[] lines)
    {
        var jobsLine = Array.FindIndex(lines, l => Regex.IsMatch(l, @"\Ajobs:[ \t]*(?:#.*)?\z"));
        var result = ImmutableList.CreateBuilder<(string, int, int)>();
        (string Key, int Start)? open = null;
        for (var i = jobsLine + 1; i < lines.Length; i++)
        {
            var key = JobKeyOf(lines[i]);
            var endsMap = EndsJobsMap(lines[i]);
            if (key is null && !endsMap)
                continue;
            if (open is { } o)
                result.Add((o.Key, o.Start, i));
            open = key is null ? null : (key, i);
            if (endsMap)
                break;
        }
        if (open is { } last)
            result.Add((last.Key, last.Start, lines.Length));
        return result.ToImmutable();
    }

    /// <summary>The <c>[Start, End)</c> line range of <paramref name="job"/> (see <see cref="Ranges"/>), or <c>null</c> when it is absent.</summary>
    internal static (int Start, int End)? Range(string[] lines, string job) =>
        Ranges(lines).FirstOrDefault(r => r.Key == job) is { Key: not null } r ? (r.Start, r.End) : null;

    /// <summary>
    /// <paramref name="job"/>'s block, verbatim — key line included, comments kept — or <c>null</c>
    /// when the job is absent. The text-in, text-out form of <see cref="Range"/>.
    /// </summary>
    internal static string? Block(string text, string job)
    {
        var lines = text.Split('\n');
        return Range(lines, job) is { } r ? string.Join("\n", lines[r.Start..r.End]) : null;
    }

    /// <summary>
    /// The step whose <c>- name:</c> line contains <paramref name="nameFragment"/>: that line through
    /// the line before the next non-comment line at the same or a shallower indentation — the next
    /// step, or the end of the <c>steps:</c> list. Bounded at BOTH ends, so the last step of a job
    /// never swallows the job after it: the hand-rolled <c>IndexOf("\n      - name:")</c> slicers
    /// ran on to the next step ANYWHERE, which for a job's last step is the first step of the
    /// following job. <c>null</c> when no step's name carries the fragment.
    /// </summary>
    internal static string? Step(string text, string nameFragment)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l =>
            l.TrimStart().StartsWith("- name:", StringComparison.Ordinal)
            && l.Contains(nameFragment, StringComparison.Ordinal));
        if (start < 0)
            return null;
        var indent = Indent(lines[start]);
        var end = Array.FindIndex(lines, start + 1, l =>
            l.Trim().Length > 0 && !l.TrimStart().StartsWith('#') && Indent(l) <= indent);
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>A column-0 key (<c>env:</c>, <c>on:</c>, …) after the jobs ends the <c>jobs:</c> map.
    /// A column-0 COMMENT does not — YAML allows it anywhere, and it ends nothing.</summary>
    private static bool EndsJobsMap(string line) =>
        line.Length > 0 && line[0] is not (' ' or '#' or '\t' or '\r');

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}

/// <summary>
/// The reader's own self-test. Without it the diagnosis above is a comment: the refusal path runs
/// only on a workflow nobody has written yet, so nothing would notice it going back to naming the
/// wrong thing.
/// </summary>
public class WorkflowJobTextTest
{
    private const string Lane = "fake-lane.yml";

    private const string Job = """

  select:
    runs-on: ubuntu-latest
    steps:
      # a comment the reader accepts
      - name: Something
        run: echo hi

  next:
    runs-on: ubuntu-latest
""";

    [Fact]
    public void AWellFormedJob_ReadsToTheNextJob_WithItsCommentsDropped()
    {
        var (body, refusal) = WorkflowJobText.Read("on: push\n" + Job, Lane, "select");

        Assert.Null(refusal);
        Assert.Contains("runs-on: ubuntu-latest", body!, StringComparison.Ordinal);
        Assert.Contains("- name: Something", body!, StringComparison.Ordinal);
        Assert.DoesNotContain("a comment the reader accepts", body!, StringComparison.Ordinal);
        // It stopped AT the next job rather than swallowing it.
        Assert.DoesNotContain("next:", body!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingJob_IsStillReportedAsAMissingJob()
    {
        var (body, refusal) = WorkflowJobText.Read("on: push\n" + Job, Lane, "pack");

        Assert.Null(body);
        Assert.Equal($"{Lane} must have a `pack` job", refusal);
    }

    /// <summary>
    /// 🚨 The case that cost MeshWeaver#4603 its afternoon: the job is present, one comment line is
    /// at column 0, and the old reader said the job did not exist. The refusal must name the LINE.
    /// </summary>
    [Fact]
    public void AColumnZeroCommentInsideAJob_NamesTheLine_NotAMissingJob()
    {
        var text = ("on: push\n" + Job).Replace(
            "      # a comment the reader accepts",
            "      # a comment the reader accepts\n# and a continuation that lost its indentation",
            StringComparison.Ordinal);

        var (body, refusal) = WorkflowJobText.Read(text, Lane, "select");

        Assert.Null(body);
        Assert.DoesNotContain("must have a `select` job", refusal!, StringComparison.Ordinal);
        Assert.Contains("# and a continuation that lost its indentation", refusal!, StringComparison.Ordinal);
        // The line NUMBER, so the reader does not have to search for it. In "on: push\n" + Job:
        // 1 `on: push`, 2 blank, 3 `  select:`, 4 `runs-on`, 5 `steps:`, 6 the comment the Replace
        // above keeps, 7 the continuation it appends — which is the one that loses its indentation.
        Assert.Contains("stops at line 7", refusal!, StringComparison.Ordinal);
        Assert.Contains("column 0", refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workflow whose jobs collide by PREFIX (<c>build-image</c> before <c>build</c>, as
    /// <c>portal-image</c> / <c>portal-image-mirror</c> do in main-cd.yml), with a trigger key that
    /// looks like a job and a last step that a step slicer could run past.
    /// </summary>
    private const string PrefixLane = """
on:
  build:
    types: [requested]

jobs:
  build-image:
    runs-on: ubuntu-latest
    steps:
      - name: Push the image
        run: echo IMAGE

  build:
    needs: [build-image]
    runs-on: ubuntu-latest
  # a two-space comment between the job's keys
    steps:
      - name: Compile
        run: echo COMPILE
      - name: Last step of build
        run: echo LAST

  verdict:
    runs-on: ubuntu-latest
    steps:
      - name: Report
        run: echo VERDICT
""";

    /// <summary>
    /// 🚨 The prefix collision, with its negative control. The shape the guards hand-rolled —
    /// <c>IndexOf("\n  " + job)</c> with the job named WITHOUT its colon — lands on
    /// <c>build-image:</c>, so every assertion about <c>build</c> would have judged the image job.
    /// The shared reader matches the key whole, skips the <c>on:</c> trigger of the same name,
    /// and keeps a two-space comment inside the job rather than ending the job at it.
    /// </summary>
    [Fact]
    public void AJobWhoseNameIsAPrefixOfAnother_IsSlicedByItsWholeKey()
    {
        // Negative control: the old shape picks the wrong job.
        var old = PrefixLane.IndexOf("\n  " + "build", StringComparison.Ordinal);
        Assert.StartsWith("\n  build:\n    types:", PrefixLane[old..], StringComparison.Ordinal); // the TRIGGER
        var oldAfterJobs = PrefixLane.IndexOf("\n  " + "build", PrefixLane.IndexOf("\njobs:", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.StartsWith("\n  build-image:", PrefixLane[oldAfterJobs..], StringComparison.Ordinal); // the WRONG job

        var block = WorkflowJobText.Block(PrefixLane, "build");

        Assert.NotNull(block);
        Assert.StartsWith("  build:\n    needs: [build-image]", block, StringComparison.Ordinal);
        Assert.Contains("echo COMPILE", block, StringComparison.Ordinal);
        Assert.Contains("echo LAST", block, StringComparison.Ordinal);
        Assert.Contains("# a two-space comment", block, StringComparison.Ordinal);
        Assert.DoesNotContain("echo IMAGE", block, StringComparison.Ordinal);
        Assert.DoesNotContain("echo VERDICT", block, StringComparison.Ordinal);
        Assert.DoesNotContain("types: [requested]", block, StringComparison.Ordinal);

        Assert.Contains("echo IMAGE", WorkflowJobText.Block(PrefixLane, "build-image")!, StringComparison.Ordinal);
        Assert.DoesNotContain("echo COMPILE", WorkflowJobText.Block(PrefixLane, "build-image")!, StringComparison.Ordinal);
        Assert.Null(WorkflowJobText.Block(PrefixLane, "buil"));
        Assert.Equal(new[] { "build-image", "build", "verdict" }, WorkflowJobText.Ranges(PrefixLane.Split('\n')).Select(r => r.Key));
    }

    /// <summary>
    /// The step slice ends with its job. Negative control: the hand-rolled shape — from the name to
    /// the next <c>"\n      - name:"</c> anywhere — runs from the last step of <c>build</c> into the
    /// first step of <c>verdict</c>.
    /// </summary>
    [Fact]
    public void TheLastStepOfAJob_DoesNotSwallowTheNextJob()
    {
        var at = PrefixLane.IndexOf("Last step of build", StringComparison.Ordinal);
        var next = PrefixLane.IndexOf("\n      - name:", at, StringComparison.Ordinal);
        Assert.Contains("verdict:", PrefixLane[at..next], StringComparison.Ordinal); // the old slice crossed a job

        var step = WorkflowJobText.Step(PrefixLane, "Last step of build");

        Assert.NotNull(step);
        Assert.Contains("echo LAST", step, StringComparison.Ordinal);
        Assert.DoesNotContain("verdict:", step, StringComparison.Ordinal);
        Assert.DoesNotContain("echo COMPILE", WorkflowJobText.Step(PrefixLane, "Last step of build")!, StringComparison.Ordinal);
        Assert.Equal("      - name: Compile\n        run: echo COMPILE", WorkflowJobText.Step(PrefixLane, "Compile"));
    }
}
