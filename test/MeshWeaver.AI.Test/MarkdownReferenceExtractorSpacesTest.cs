using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// <see cref="MarkdownReferenceExtractor"/> against paths containing SPACES — the quoted
/// <c>@"path"</c> form, the legacy <c>@("path")</c> form, both <c>/</c> and <c>:</c> separators, and
/// removal by path.
/// <para>
/// These moved out of <c>MeshWeaver.Content.Test.SpacesInFileNameTest</c> (#2276):
/// <see cref="MarkdownReferenceExtractor"/> ships with <c>MeshWeaver.AI</c>, and it was the only
/// thing keeping core's content suite referencing the AI assembly. The rest of that test — the UCR
/// prefix resolver and the autocomplete provider — is genuinely core and stays there.
/// </para>
/// <para>
/// No mesh is needed: every method under test is static and pure, so this is a plain xUnit class
/// rather than a <c>MonolithMeshTestBase</c> derivative.
/// </para>
/// </summary>
public class MarkdownReferenceExtractorSpacesTest(ITestOutputHelper output)
{

    [Theory]
    [InlineData("see @\"content/My Report.md\" for details", "content/My Report.md")]
    [InlineData("embed @\"content/My Documents/Budget Plan.xlsx.md\"", "content/My Documents/Budget Plan.xlsx.md")]
    [InlineData("check @\"ACME/content/Team Photo.svg\"", "ACME/content/Team Photo.svg")]
    [InlineData("@\"content/Q1 2025 Results.pdf\"", "content/Q1 2025 Results.pdf")]
    public void MarkdownExtractor_QuotedPaths_ExtractsCorrectly(string markdown, string expectedPath)
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);

        output.WriteLine($"Input: {markdown}");
        output.WriteLine($"Extracted paths: [{string.Join(", ", paths)}]");

        paths.Should().ContainSingle()
            .Which.Should().Be(expectedPath);
    }

    [Theory]
    [InlineData("@\"content:My Report.md\"", "content:My Report.md")]
    [InlineData("@\"content/My Report.md\"", "content/My Report.md")]
    public void MarkdownExtractor_QuotedPaths_BothFormats(string markdown, string expectedPath)
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);
        paths.Should().ContainSingle().Which.Should().Be(expectedPath);
    }

    [Fact]
    public void MarkdownExtractor_QuotedReference_RemovedCorrectly()
    {
        var input = "see @\"content/My Report.md\" for details";
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(input, "content/My Report.md");

        output.WriteLine($"Input:  {input}");
        output.WriteLine($"Result: '{result}'");

        result.Should().Be("see for details");
    }

    [Fact]
    public void MarkdownExtractor_MixedQuotedAndUnquoted_AllExtracted()
    {
        var markdown = "compare @ACME/Reports with @\"content/My Report.md\" and @simple.md";
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);

        output.WriteLine($"Paths: [{string.Join(", ", paths)}]");

        paths.Should().HaveCount(3);
        paths.Should().Contain("ACME/Reports");
        paths.Should().Contain("content/My Report.md");
        paths.Should().Contain("simple.md");
    }

    [Fact]
    public void MarkdownExtractor_ParenthesesQuotedPaths_ExtractedCorrectly()
    {
        // Legacy @("path") format also supports spaces
        var markdown = "see @(\"content/My Report.md\") for details";
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);

        paths.Should().ContainSingle().Which.Should().Be("content/My Report.md");
    }
}
