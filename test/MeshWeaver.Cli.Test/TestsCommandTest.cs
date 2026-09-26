using MeshWeaver.Cli;
using Xunit;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// <c>memex tests</c> reads a node's Tests area frame by frame. Pinned over the PURE seam: which
/// frame is progress and which is the verdict, what a verdict says, and that a row is printed again
/// only when its state or output changed — never for a ticking clock.
/// </summary>
public class TestsCommandTest
{
    private const string Progress = """
        {"areas":{
          "\"$Menu:Node\"":{"items":[{"label":"Request approval","icon":"✅"}]},
          "\"Tests\"":{"$type":"StackControl","id":"tests-running","areas":[{"area":"Tests/Title"},{"area":"Tests/Cases"}]},
          "\"Tests/Title\"":{"$type":"HtmlControl","data":"<h2>Store tests — 1 of 3 done, running for 4s</h2>"},
          "\"Tests/Cases\"":{"$type":"DataGridControl","data":[
            {"class":"Store","case":"first","result":"✔","time":"0.1s","output":""},
            {"class":"Store","case":"slow","result":"▶","time":"3.9s","output":"contacted the service"},
            {"class":"Store","case":"last","result":"⏳","time":"","output":""}]}}}
        """;

    private const string Verdict = """
        {"areas":{
          "\"Tests\"":{"$type":"StackControl","areas":[{"area":"Tests/Title"},{"area":"Tests/Cases"}]},
          "\"Tests/Title\"":{"$type":"HtmlControl","data":"<h2>Store tests — 2/3 passed</h2>"},
          "\"Tests/Cases\"":{"$type":"DataGridControl","data":[
            {"class":"Store","case":"first","result":"✅ pass","time":"0.1s","output":""},
            {"class":"Store","case":"slow","result":"❌ FAIL","time":"45.0s","output":"timed out: no verdict within 45s · contacted the service"},
            {"class":"Store","case":"last","result":"✅ pass","time":"0.0s","output":""}]}}}
        """;

    [Fact]
    public void A_progress_frame_is_running_and_names_every_case()
    {
        var frame = TestsCommand.Parse(Progress);
        Assert.True(frame.Running);
        Assert.Equal("Store tests — 1 of 3 done, running for 4s", frame.Title);
        Assert.Equal(["first", "slow", "last"], frame.Rows.Select(r => r.Case));
        Assert.Equal("contacted the service", frame.Rows[1].Output);
    }

    [Fact]
    public void The_verdict_frame_fails_on_a_failed_case_and_the_chrome_never_counts()
    {
        var frame = TestsCommand.Parse(Verdict);
        Assert.False(frame.Running);
        Assert.False(frame.Passed);

        var green = TestsCommand.Parse(Verdict.Replace("❌ FAIL", "✅ pass").Replace("2/3", "3/3"));
        Assert.True(green.Passed);
    }

    [Fact]
    public void A_row_is_reprinted_only_when_its_state_or_output_changes()
    {
        var printed = new Dictionary<string, TestsCommand.Row>();
        Assert.Equal(3, TestsCommand.Changes(printed, TestsCommand.Parse(Progress)).Count);
        Assert.Empty(TestsCommand.Changes(printed, TestsCommand.Parse(Progress.Replace("3.9s", "4.9s"))));

        var verdictLines = TestsCommand.Changes(printed, TestsCommand.Parse(Verdict));
        Assert.Equal(3, verdictLines.Count);
        Assert.Contains(verdictLines, l => l.Contains("timed out", StringComparison.Ordinal));
    }
}
