#pragma warning disable CS1591

using System;
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
}
