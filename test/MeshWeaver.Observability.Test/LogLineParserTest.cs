using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// The fingerprint decides how many GitHub issues a production incident produces, so these tests
/// pin BOTH directions: occurrences of one fault must collapse to one fingerprint, and genuinely
/// different faults must not.
/// </summary>
public class LogLineParserTest
{
    private static readonly string[] NodeNotFound =
    [
        "fail: MeshWeaver.Data.MeshDataSource[0]",
        "      Update failed for node rbuergi/Foo/7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b",
        "      System.InvalidOperationException: Sequence contains no elements",
        "         at MeshWeaver.Data.MeshDataSource.Apply(MeshNode node) in /src/MeshWeaver.Data/MeshDataSource.cs:line 88",
        "         at System.Reactive.Linq.ObservableImpl.Select`2.Selector.OnNext(TSource value)",
    ];

    [Fact]
    public void Parse_ExtractsCategoryExceptionAndApplicationFrame()
    {
        var burst = LogLineParser.Parse(NodeNotFound);

        burst.Should().NotBeNull();
        burst!.Severity.Should().Be(LogSeverity.Error);
        burst.Category.Should().Be("MeshWeaver.Data.MeshDataSource");
        burst.ExceptionType.Should().Be("System.InvalidOperationException");
        // The FRAMEWORK frame (System.Reactive…) must be skipped — it is identical for every
        // reactive fault and would collapse unrelated errors onto one fingerprint.
        burst.TopFrame.Should().Be("MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)");
        // …and the :line suffix must be gone, or an unrelated edit above forks the fingerprint.
        burst.TopFrame.Should().NotContain("line 88");
    }

    [Fact]
    public void Fingerprint_IsStableAcrossVolatileValues()
    {
        var other = NodeNotFound.ToArray();
        other[1] = "      Update failed for node acme/Bar/91bcd7e0-1234-4f88-9a01-bbccddeeff00";

        var first = LogLineParser.Fingerprint(LogLineParser.Parse(NodeNotFound)!);
        var second = LogLineParser.Fingerprint(LogLineParser.Parse(other)!);

        // Same defect, different node id → ONE incident, ONE ticket.
        second.Should().Be(first);
    }

    [Fact]
    public void Fingerprint_CollapsesTheSameFaultAcrossPartitions()
    {
        var other = NodeNotFound.ToArray();
        other[1] = "      Update failed for node acme/Bar/91bcd7e0-1234-4f88-9a01-bbccddeeff00";

        // The partition prefix is per-user. If it survived normalization, one defect would open a
        // separate ticket for every tenant that hit it — the exact flood this exists to prevent.
        LogLineParser.Parse(other)!.NormalizedMessage
            .Should().Be(LogLineParser.Parse(NodeNotFound)!.NormalizedMessage);
    }

    [Fact]
    public void Fingerprint_DiffersForADifferentFault()
    {
        var different = NodeNotFound.ToArray();
        different[2] = "      System.NullReferenceException: Object reference not set to an instance of an object.";

        var first = LogLineParser.Fingerprint(LogLineParser.Parse(NodeNotFound)!);
        var second = LogLineParser.Fingerprint(LogLineParser.Parse(different)!);

        second.Should().NotBe(first);
    }

    [Fact]
    public void Fingerprint_DiffersForADifferentCategory()
    {
        var different = NodeNotFound.ToArray();
        different[0] = "fail: MeshWeaver.Graph.MeshCatalog[0]";

        LogLineParser.Fingerprint(LogLineParser.Parse(different)!)
            .Should().NotBe(LogLineParser.Fingerprint(LogLineParser.Parse(NodeNotFound)!));
    }

    [Theory]
    [InlineData("fail: Some.Category[0]", true, LogSeverity.Error)]
    [InlineData("crit: Some.Category[0]", true, LogSeverity.Critical)]
    [InlineData("warn: Some.Category[0]", false, default(LogSeverity))]
    [InlineData("info: Some.Category[0]", false, default(LogSeverity))]
    [InlineData("      at Foo.Bar()", false, default(LogSeverity))]
    public void IsRedHeader_MatchesOnlyErrorAndCritical(string line, bool expected, LogSeverity severity)
    {
        LogLineParser.IsRedHeader(line, out var actual).Should().Be(expected);
        if (expected)
            actual.Should().Be(severity);
    }

    [Fact]
    public void Parse_HandlesABurstWithNoStackTrace()
    {
        var burst = LogLineParser.Parse(
        [
            "crit: Memex.Portal.Startup[0]",
            "      Could not connect to the database after 5 attempts",
        ]);

        burst.Should().NotBeNull();
        burst!.Severity.Should().Be(LogSeverity.Critical);
        burst.Category.Should().Be("Memex.Portal.Startup");
        burst.ExceptionType.Should().BeNull();
        burst.TopFrame.Should().BeNull();
        // The attempt count is masked, so "after 5" and "after 9" are one fingerprint.
        burst.NormalizedMessage.Should().NotContain("5");
    }

    [Fact]
    public void Parse_ReturnsNullForANonRedBurst()
        => LogLineParser.Parse(["warn: Some.Category[0]", "      just a warning"]).Should().BeNull();

    [Fact]
    public void Normalize_MasksTheVolatileParts()
    {
        var normalized = LogLineParser.Normalize(
            "Node 7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b at /var/lib/data/file.json failed 42 times "
            + "at 2026-08-07T11:22:33Z for 'some-value'");

        normalized.Should().Contain("{guid}");
        normalized.Should().Contain("{path}");
        normalized.Should().Contain("{n}");
        normalized.Should().Contain("{time}");
        normalized.Should().Contain("'{value}'");
    }

    /// <summary>
    /// A message that names WHO it is about must not fork the fingerprint per subject. Production
    /// 2026-08-08: one ROUTER_TRAFFIC defect, reported once per target hub, produced ~50 incidents —
    /// and would have produced ~50 tickets — because `target: Claims`, `target: Edu`, `target: X`
    /// each normalized differently. `sender:` was already masked (it held a mesh path); `target:`
    /// held a bare word and survived.
    /// </summary>
    [Fact]
    public void Fingerprint_DoesNotForkOnTheSubjectOfTheMessage()
    {
        const string template =
            "ROUTER_TRAFFIC: RawJson has the mesh hub as sender (sender: mesh/N-u6rl0oAUuc, target: {0}). "
            + "The mesh hub is the ROUTER and must not execute work.";

        var a = LogLineParser.Normalize(string.Format(template, "Claims"));
        var b = LogLineParser.Normalize(string.Format(template, "Edu"));
        var c = LogLineParser.Normalize(string.Format(template, "X"));

        b.Should().Be(a);
        c.Should().Be(a);
        a.Should().Contain("target: {id}");
    }

    [Fact]
    public void Normalize_LeavesOrdinaryProseAlone()
    {
        // The masking is keyed on a known label + a SINGLE token, so a real message keeps its words —
        // over-masking would collapse genuinely different faults onto one fingerprint, which is the
        // failure this system started with.
        var normalized = LogLineParser.Normalize("Sequence contains no elements");

        normalized.Should().Be("Sequence contains no elements");
    }

    [Fact]
    public void Normalize_DoesNotDoubleMaskAnAlreadyMaskedValue()
    {
        // `sender:` is followed by a path that the path rule already turned into {path}; the
        // identifier rule must not then rewrite it to {id} and undo the distinction.
        var normalized = LogLineParser.Normalize("delivery (sender: rbuergi/Foo/Bar, target: Edu)");

        normalized.Should().Contain("{path}");
        normalized.Should().Contain("target: {id}");
    }
}
