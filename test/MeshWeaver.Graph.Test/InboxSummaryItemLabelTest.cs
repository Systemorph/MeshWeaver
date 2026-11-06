using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The outcome line of a bulk act names each item that was not done the way the viewer saw it in
/// the list — its title and who it is from — never by its node path (owner, 2026-11-06: nine
/// approvals came back as "rbuergi/_Notification/BZdc…: could not be changed" — "can you put from
/// whom it is, title").
/// </summary>
public class InboxSummaryItemLabelTest
{
    private static readonly InboxRow Row = new()
    {
        Path = "alice/_Notification/abc", Title = "Waiting on you: acme#12", From = "Bug-fix pool",
    };

    [Fact]
    public void AnItemIsNamedByItsTitleAndSender_NotItsPath()
    {
        var label = InboxLayoutArea.ItemLabel([Row], "alice/_Notification/abc");
        Assert.Equal("“Waiting on you: acme#12” (Bug-fix pool)", label);
        Assert.DoesNotContain("_Notification", label);
    }

    [Fact]
    public void AnEmptySenderOrOneThatRepeatsTheTitleIsLeftOut()
    {
        Assert.Equal("“Waiting on you: acme#12”", InboxLayoutArea.ItemLabel([Row with { From = "" }], Row.Path));
        Assert.Equal("“Waiting on you: acme#12”", InboxLayoutArea.ItemLabel([Row with { From = "Waiting on you: acme#12" }], Row.Path));
    }

    [Fact]
    public void WithoutAKnownRowThePathIsTheFallback()
    {
        Assert.Equal("bob/_Notification/x", InboxLayoutArea.ItemLabel([Row], "bob/_Notification/x"));
        Assert.Equal("bob/_Notification/x", InboxLayoutArea.ItemLabel(null, "bob/_Notification/x"));
        Assert.Equal(Row.Path, InboxLayoutArea.ItemLabel([Row with { Title = " " }], Row.Path));
    }
}
