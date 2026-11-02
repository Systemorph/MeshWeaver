using System.Text.RegularExpressions;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Text must take a TEXT token. <c>--accent-fill-rest</c> and <c>--error</c> are fills: their
/// lightness is chosen for white text ON them, so as a text colour they fall below 4.5:1 on the
/// hover and pressed surfaces (the default blue: 4.47:1 on surface-2; the danger red: 4.41:1 on
/// surface-3). <c>--mw-accent-text</c> and <c>--mw-danger-text</c> are solved against every
/// surface (DesignTokensTest), so a <c>color:</c> declaration in the Graph sources reads those.
/// </summary>
public class TextColourTokensTest
{
    private static readonly Regex FillAsText =
        new(@"(?<![-\w])color:\s*var\(--(accent-fill-[a-z]+|error)(?![-\w])", RegexOptions.Compiled);

    [Fact]
    public void NoTextColour_ReadsAFillToken()
    {
        var offenders = Directory
            .EnumerateFiles(RepoPaths.Combine("src", "MeshWeaver.Graph"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => FillAsText.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        offenders.Should().BeEmpty("a fill token as a text colour misses 4.5:1 on the hover and pressed surfaces");
    }

    [Fact]
    public void ThePattern_CatchesTheFillAndSparesTheBackground()
    {
        FillAsText.IsMatch("headlineStyle = \"color: var(--accent-fill-rest);\"").Should().BeTrue();
        FillAsText.IsMatch("style += \" color: var(--error, #d32f2f);\"").Should().BeTrue();
        FillAsText.IsMatch("background-color: var(--error)").Should().BeFalse();
        FillAsText.IsMatch("color: var(--error-foreground)").Should().BeFalse();
        FillAsText.IsMatch("color: var(--mw-danger-text, var(--error))").Should().BeFalse();
    }
}
