using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for PathEscaping utility.
/// </summary>
public class PathEscapingTest
{
    [Theory(Timeout = 5000)]
    [InlineData("simple", "simple")]
    [InlineData("with/slash", "with__slash")]
    [InlineData("multiple/slashes/here", "multiple__slashes__here")]
    [InlineData("", "")]
    [InlineData("/leading", "__leading")]
    [InlineData("trailing/", "trailing__")]
    [InlineData("back\\slash", "back__slash")]
    public void Escape_ReplacesSlashesWithDoubleUnderscore(string input, string expected)
    {
        // Act
        var result = PathEscaping.Escape(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory(Timeout = 5000)]
    [InlineData("simple", "simple")]
    [InlineData("with__slash", "with/slash")]
    [InlineData("multiple__slashes__here", "multiple/slashes/here")]
    [InlineData("", "")]
    [InlineData("__leading", "/leading")]
    [InlineData("trailing__", "trailing/")]
    public void Unescape_ReplacesDoubleUnderscoreWithSlash(string input, string expected)
    {
        // Act
        var result = PathEscaping.Unescape(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory(Timeout = 5000)]
    [InlineData("test/path")]
    [InlineData("graph/org/project")]
    [InlineData("a/b/c/d/e")]
    public void RoundTrip_EscapeAndUnescape_ReturnsOriginal(string original)
    {
        // Act
        var escaped = PathEscaping.Escape(original);
        var unescaped = PathEscaping.Unescape(escaped);

        // Assert
        unescaped.Should().Be(original);
    }

    [Fact(Timeout = 5000)]
    public void Escape_NullInput_ReturnsNull()
    {
        // Act
        var result = PathEscaping.Escape(null!);

        // Assert
        result.Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public void Unescape_NullInput_ReturnsNull()
    {
        // Act
        var result = PathEscaping.Unescape(null!);

        // Assert
        result.Should().BeNull();
    }
}
