using FluentAssertions;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class MeshNodeImageHelperTest
{
    [Theory]
    [InlineData("Document", null)]
    [InlineData("People", null)]
    [InlineData("/images/logo.png", "/images/logo.png")]
    [InlineData("data:image/png;base64,abc", "data:image/png;base64,abc")]
    [InlineData("https://example.com/img.png", "https://example.com/img.png")]
    [InlineData("path/to/image.png", "path/to/image.png")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GetIconAsImageUrl_ReturnsExpected(string? icon, string? expected)
    {
        MeshNodeImageHelper.GetIconAsImageUrl(icon).Should().Be(expected);
    }
}
