using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The sentinel WRITERS and the sentinel CLASSIFIER agree — pinned at the source, because the
/// verbs write their sentinels as interpolated literals at some forty sites in
/// <c>MeshOperations.cs</c> and <see cref="OperationSentinel"/> reads them by prefix. A writer that
/// drifted ("Error -", "Not Found:", a leading space) would ship a 200 with prose again, exactly
/// the shape MeshWeaver.Plugins#1699 measured, while every classifier test stayed green.
/// </summary>
public class OperationSentinelPrefixTest
{
    private static readonly Regex SentinelLiteral = new(
        """(?<q>\$?@?\$?")(?<text>(?:Error|Not found|Unavailable)\b[^"]{0,40})""", RegexOptions.Compiled);

    [Fact]
    public void Every_sentinel_literal_the_verbs_write_is_one_the_classifier_reads()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MeshWeaver.Mesh.Operations", "MeshOperations.cs"));
        var literals = SentinelLiteral.Matches(source)
            .Select(m => m.Groups["text"].Value)
            .Where(text => text.Contains(':'))                    // the sentinel shape is "<Word>: …"
            .Distinct(StringComparer.Ordinal)
            .ToList();

        literals.Should().NotBeEmpty("the census must see the writers, or it measures nothing");
        literals.Count.Should().BeGreaterThan(10, "MeshOperations writes its sentinels at dozens of sites");

        var unclassified = literals
            .Where(text => OperationSentinel.Classify(text.Replace("{", "x").Replace("}", "x")) is null)
            .ToList();
        unclassified.Should().BeEmpty(
            "every sentinel the verbs write must be read by the classifier, or the REST mirror ships it as a 200 again: {0}",
            string.Join(" | ", unclassified));
    }

    [Fact]
    public void The_constants_are_the_prefixes_the_writers_use()
    {
        OperationSentinel.ErrorPrefix.Should().Be("Error:");
        OperationSentinel.NotFoundPrefix.Should().Be("Not found:");
        OperationSentinel.UnavailablePrefix.Should().Be("Unavailable:");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
