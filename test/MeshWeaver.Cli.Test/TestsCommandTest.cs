using MeshWeaver.Cli;
using Xunit;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// <c>memex tests</c> prints a Tests run's ACTIVITY line by line (it never polls the Tests area,
/// which would re-run the suite on every read). Pinned over the pure seam: what an activity read
/// says, and which lines are new — including the lines that slid out of the window unseen.
/// </summary>
public class TestsCommandTest
{
    private const string Running = """
        {"$type":"MeshNode","path":"Roland/_Activity/ab12cd34","content":{"$type":"ActivityLog",
          "status":"Running","messageCount":3,
          "messages":[{"message":"Run the Tests area of Store/Maintenance"},{"message":"▶ slow (1.0s) — contacted the service"},{"message":"✔ first (0.1s)"}]}}
        """;

    private const string Failed = """
        {"$type":"MeshNode","path":"Roland/_Activity/ab12cd34","content":{"$type":"ActivityLog",
          "status":"Failed","messageCount":6,
          "messages":[{"message":"✔ first (0.1s)"},{"message":"❌ FAIL slow (45.0s) — timed out: no verdict within 45s"},{"message":"Store tests — 1/2 passed"},{"message":"Store tests: not every case passed."}]}}
        """;

    [Fact]
    public void An_activity_read_carries_its_status_count_and_window()
    {
        var read = TestsCommand.ParseActivity(Running);
        Assert.Equal("Running", read.Status);
        Assert.False(read.Terminal);
        Assert.Equal(3, read.MessageCount);
        Assert.Equal(3, read.Window.Length);
    }

    [Fact]
    public void Only_new_lines_print_and_archived_ones_are_announced()
    {
        Assert.Equal(3, TestsCommand.NewLines(0, TestsCommand.ParseActivity(Running)).Length);
        Assert.Empty(TestsCommand.NewLines(3, TestsCommand.ParseActivity(Running)));

        var failed = TestsCommand.ParseActivity(Failed);
        Assert.True(failed.Terminal);
        Assert.Equal(["❌ FAIL slow (45.0s) — timed out: no verdict within 45s", "Store tests — 1/2 passed", "Store tests: not every case passed."],
            TestsCommand.NewLines(3, failed));

        var afterAGap = TestsCommand.NewLines(0, failed);
        Assert.StartsWith("… 2 line(s) were archived", afterAGap[0]);
        Assert.Equal(5, afterAGap.Length);
    }
}
