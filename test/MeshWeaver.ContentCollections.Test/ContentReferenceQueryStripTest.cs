using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// An embed such as <c>@@node/$Content</c> reaches the content area with the id <c>?showHeader=false</c>;
/// the query is a parameter and must never be read as the collection path.
/// </summary>
public class ContentReferenceQueryStripTest
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("?showHeader=false", "")]
    [InlineData("content/a.png", "content/a.png")]
    [InlineData("content/a.png?showHeader=false", "content/a.png")]
    public void StripQuery_RemovesTheQueryString(string? id, string expected)
        => Assert.Equal(expected, ContentLayoutArea.StripQuery(id));
}
