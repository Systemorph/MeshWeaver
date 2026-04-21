using FluentAssertions;
using MeshWeaver.NuGet;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class NuGetDirectiveParserTest
{
    [Fact(Timeout = 5000)]
    public void SingleDirectiveWithVersion_ExtractsAndStrips()
    {
        var (cleaned, refs) = NuGetDirectiveParser.Extract(
            """
            #r "nuget:Humanizer, 2.14.1"
            using Humanizer;
            "hello".Humanize()
            """);

        refs.Should().ContainSingle();
        refs[0].Id.Should().Be("Humanizer");
        refs[0].VersionRange.Should().Be("2.14.1");
        cleaned.Should().NotContain("nuget:");
        cleaned.Should().Contain("using Humanizer;");
    }

    [Fact(Timeout = 5000)]
    public void MultipleDirectives_AllCaptured()
    {
        var (cleaned, refs) = NuGetDirectiveParser.Extract(
            """
            #r "nuget:Humanizer, 2.14.1"
            #r "nuget:Markdig, 0.37.0"
            using Humanizer;
            """);

        refs.Should().HaveCount(2);
        refs.Should().ContainEquivalentOf(new NuGetPackageReference("Humanizer", "2.14.1"));
        refs.Should().ContainEquivalentOf(new NuGetPackageReference("Markdig", "0.37.0"));
        cleaned.Should().NotContain("nuget:");
    }

    [Fact(Timeout = 5000)]
    public void NoVersion_VersionRangeIsNull()
    {
        var (_, refs) = NuGetDirectiveParser.Extract("#r \"nuget:Humanizer\"");
        refs.Should().ContainSingle();
        refs[0].Id.Should().Be("Humanizer");
        refs[0].VersionRange.Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public void NonNuGetDirective_LeftAlone()
    {
        var source = """
            #r "System.Text.Json"
            #r "file:C:/lib/Foo.dll"
            using System.Text.Json;
            """;
        var (cleaned, refs) = NuGetDirectiveParser.Extract(source);

        refs.Should().BeEmpty();
        cleaned.Should().Contain("#r \"System.Text.Json\"");
        cleaned.Should().Contain("#r \"file:C:/lib/Foo.dll\"");
    }

    [Fact(Timeout = 5000)]
    public void NoDirective_SourceUnchanged()
    {
        const string source = "using System;\nConsole.WriteLine(\"hi\");";
        var (cleaned, refs) = NuGetDirectiveParser.Extract(source);
        refs.Should().BeEmpty();
        cleaned.Should().Be(source);
    }

    [Fact(Timeout = 5000)]
    public void WhitespaceVariants_Handled()
    {
        var (_, refs) = NuGetDirectiveParser.Extract(
            "   #r   \"nuget:  MathNet.Numerics ,  5.0.0  \"\nusing MathNet.Numerics;");
        refs.Should().ContainSingle();
        refs[0].Id.Should().Be("MathNet.Numerics");
        refs[0].VersionRange.Should().Be("5.0.0");
    }
}
