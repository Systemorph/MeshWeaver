using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="TestsAreaFrame"/> reads a streamed Tests area off the wire for a consumer that is not
/// the page (<c>MeshOperations.RunTests</c>). Pinned: a progress frame is transient, chrome never
/// counts, two classes' same-named cases stay apart, and a ticking clock is not a change.
/// </summary>
public class TestsAreaFrameTest
{
    private static JsonElement Frame(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static readonly JsonElement Progress = Frame("""
        {"areas":{
          "\"$Menu:Node\"":{"items":[{"label":"Request approval","icon":"✅"}]},
          "\"Tests\"":{"$type":"StackControl","id":"tests-running","areas":[{"area":"Tests/Title"},{"area":"Tests/Cases"}]},
          "\"Tests/Title\"":{"$type":"HtmlControl","data":"<h2>Store tests — 1 of 3 done, running for 4s</h2>"},
          "\"Tests/Cases\"":{"$type":"DataGridControl","data":[
            {"class":"A","case":"same","result":"✔","time":"0.1s","output":""},
            {"class":"B","case":"same","result":"▶","time":"3.9s","output":"contacted the service"},
            {"class":"B","case":"last","result":"⏳","time":"","output":""}]}}}
        """);

    [Fact]
    public void A_progress_frame_is_transient_and_keeps_same_named_cases_apart()
    {
        var frame = TestsAreaFrame.Read(Progress);
        Assert.True(frame.Materialized);
        Assert.True(frame.Transient);
        Assert.False(frame.Passed);
        Assert.Equal("Store tests — 1 of 3 done, running for 4s", frame.Title);
        Assert.Equal(3, frame.Rows.Select(r => r.Key).Distinct().Count());
        Assert.Empty(frame.Text);   // the chrome's ✅ icon is not a verdict
    }

    [Fact]
    public void A_ticking_clock_is_not_a_change_and_a_verdict_is_read()
    {
        var (first, printed) = TestsAreaFrame.Changes(ImmutableDictionary<string, TestsAreaFrame.Row>.Empty, TestsAreaFrame.Read(Progress));
        Assert.Equal(3, first.Length);
        var ticked = TestsAreaFrame.Read(Frame(Progress.GetRawText().Replace("3.9s", "4.9s")));
        Assert.Empty(TestsAreaFrame.Changes(printed, ticked).Changed);

        var verdict = TestsAreaFrame.Read(Frame(Progress.GetRawText()
            .Replace("\"id\":\"tests-running\",", "")
            .Replace("1 of 3 done, running for 4s", "3/3 passed")
            .Replace("✔", "✅ pass").Replace("▶", "✅ pass").Replace("⏳", "✅ pass")));
        Assert.False(verdict.Transient);
        Assert.True(verdict.Passed);
    }
}
