// <meshweaver>
// Id: Testing/Graph/PathEscapingTest
// DisplayName: Testing/Graph/PathEscapingTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using MeshWeaver.Graph.Configuration;

/// <summary>
/// Tests for PathEscaping utility.
/// </summary>
public class PathEscapingTest
{
    [MeshTheory(TimeoutSeconds = 5)]
    [MeshInlineData("simple", "simple")]
    [MeshInlineData("with/slash", "with__slash")]
    [MeshInlineData("multiple/slashes/here", "multiple__slashes__here")]
    [MeshInlineData("", "")]
    [MeshInlineData("/leading", "__leading")]
    [MeshInlineData("trailing/", "trailing__")]
    [MeshInlineData("back\\slash", "back__slash")]
    public void Escape_ReplacesSlashesWithDoubleUnderscore(string input, string expected)
    {
        // Act
        var result = PathEscaping.Escape(input);

        // Assert
        result.Should().Be(expected);
    }

    [MeshTheory(TimeoutSeconds = 5)]
    [MeshInlineData("simple", "simple")]
    [MeshInlineData("with__slash", "with/slash")]
    [MeshInlineData("multiple__slashes__here", "multiple/slashes/here")]
    [MeshInlineData("", "")]
    [MeshInlineData("__leading", "/leading")]
    [MeshInlineData("trailing__", "trailing/")]
    public void Unescape_ReplacesDoubleUnderscoreWithSlash(string input, string expected)
    {
        // Act
        var result = PathEscaping.Unescape(input);

        // Assert
        result.Should().Be(expected);
    }

    [MeshTheory(TimeoutSeconds = 5)]
    [MeshInlineData("test/path")]
    [MeshInlineData("graph/org/project")]
    [MeshInlineData("a/b/c/d/e")]
    public void RoundTrip_EscapeAndUnescape_ReturnsOriginal(string original)
    {
        // Act
        var escaped = PathEscaping.Escape(original);
        var unescaped = PathEscaping.Unescape(escaped);

        // Assert
        unescaped.Should().Be(original);
    }

    [MeshFact(TimeoutSeconds = 5)]
    public void Escape_NullInput_ReturnsNull()
    {
        // Act
        var result = PathEscaping.Escape(null!);

        // Assert
        result.Should().BeNull();
    }

    [MeshFact(TimeoutSeconds = 5)]
    public void Unescape_NullInput_ReturnsNull()
    {
        // Act
        var result = PathEscaping.Unescape(null!);

        // Assert
        result.Should().BeNull();
    }
}
