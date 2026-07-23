using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="CommentLayoutAreas.OpensInEdit"/> — the initial edit-state seed of a comment's
/// Overview area. A freshly created comment is empty and must open straight in the editor (no extra ✎
/// click before typing); anything already written renders read-only until the author toggles.
/// </summary>
public class CommentEditStateTest
{
    [Theory(Timeout = 5000)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FreshEmptyComment_OpensInEdit(string? text)
        => Assert.True(CommentLayoutAreas.OpensInEdit(text));

    [Fact(Timeout = 5000)]
    public void WrittenComment_OpensReadOnly()
        => Assert.False(CommentLayoutAreas.OpensInEdit("looks good — but check the aggregate limit"));
}
