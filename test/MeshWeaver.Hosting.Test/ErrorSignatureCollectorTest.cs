using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The in-process error ledger behind instance error telemetry (Doc/Architecture/InstanceTelemetry):
/// records group by SIGNATURE (category + event id + template + exception type + top frame), never by
/// the formatted line; the ledger is bounded; a digest carries only what is new or escalated since the
/// last ACCEPTED one; and nothing that leaves the process is a raw log line or a secret.
/// </summary>
public class ErrorSignatureCollectorTest
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static (ErrorSignatureCollector Collector, ILogger Logger) Tap(string category = "MeshWeaver.Sample.Worker", int capacity = 200)
    {
        var collector = new ErrorSignatureCollector(capacity);
        using var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(new ErrorSignatureLoggerProvider(collector)));
        return (collector, factory.CreateLogger(category));
    }

    [Fact]
    public void TheSameTemplateWithDifferentValues_IsOneSignature_CarryingNoValue()
    {
        var (collector, logger) = Tap();

        logger.LogError("Failed to reach {Url} for order {OrderId}", "https://acme.example/a?sig=secret1", 4711);
        logger.LogError("Failed to reach {Url} for order {OrderId}", "https://acme.example/b?sig=secret2", 4712);
        logger.LogWarning("Failed to reach {Url} for order {OrderId}", "https://acme.example/c", 4713);

        var pending = collector.Pending();
        pending.Should().HaveCount(1, "two Error records of one template fold; the Warning is not collected");
        var signature = pending[0];
        signature.Count.Should().Be(2);
        signature.Template.Should().Be("Failed to reach {Url} for order {OrderId}");
        signature.Category.Should().Be("MeshWeaver.Sample.Worker");
        signature.Level.Should().Be("Error");
        var wire = JsonSerializer.Serialize(signature, ErrorDigestPayloads.Json);
        wire.Should().NotContain("acme.example").And.NotContain("secret1").And.NotContain("4711",
            "a digest carries the template, never the values the line was formatted with");
    }

    [Fact]
    public void DifferentTemplatesOrExceptions_AreDifferentSignatures_AndCriticalWins()
    {
        var (collector, logger) = Tap();

        logger.LogError("Import failed");
        logger.LogError(new InvalidOperationException("boom"), "Import failed");
        logger.LogCritical("Import failed");

        var snapshot = collector.Snapshot();
        snapshot.Should().HaveCount(2, "with and without an exception type are two sites");
        var plain = snapshot.Single(e => e.ExceptionType is null);
        plain.Count.Should().Be(2);
        plain.Level.Should().Be(LogLevel.Critical, "the worst level seen is kept");
        snapshot.Single(e => e.ExceptionType is not null).ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void APreFormattedMessage_IsNormalised_SoValuesAndSecretsBecomePlaceholders()
    {
        var (collector, logger) = Tap();
        var guid = Guid.NewGuid();

        // No template: the state is a plain string, as a pre-formatted LogError($"…") call produces.
        logger.Log(LogLevel.Error, 0, $"Node {guid} refused for user jane.roe@example.com with Bearer abcdefghijklmnop at /data/Store/Orders/42",
            null, (state, _) => state);

        var signature = collector.Pending().Single();
        signature.Template.Should().NotContain(guid.ToString()).And.NotContain("jane.roe").And.NotContain("abcdefghijklmnop")
            .And.NotContain("Orders/42");
        signature.Template.Should().Contain("{guid}").And.Contain(TelemetryRedaction.MaskedEmail).And.Contain(TelemetryRedaction.Masked)
            .And.Contain("{path}");
    }

    [Fact]
    public void TheLedgerIsBounded_EvictingOnlyWhatWasReported_AndCountingWhatItDropped()
    {
        var collector = new ErrorSignatureCollector(capacity: 2);

        collector.Record("A", 0, LogLevel.Error, "first", null, null, T0);
        collector.Record("A", 0, LogLevel.Error, "second", null, null, T0.AddSeconds(1));
        collector.Record("A", 0, LogLevel.Error, "third", null, null, T0.AddSeconds(2));

        collector.Count.Should().Be(2);
        collector.Dropped.Should().Be(1, "every held signature is still unreported, so the newcomer is dropped and counted");
        collector.Pending().Select(s => s.Template).Should().Equal(["first", "second"]);

        collector.MarkReported(collector.Pending());
        collector.Record("A", 0, LogLevel.Error, "fourth", null, null, T0.AddSeconds(3));

        collector.Count.Should().Be(2);
        collector.Snapshot().Select(e => e.Template).OrderBy(t => t, StringComparer.Ordinal).Should().Equal(["fourth", "second"],
            "the OLDEST reported signature made room");
        collector.Dropped.Should().Be(1);
    }

    [Fact]
    public void ADigestCarriesOnlyWhatIsNewOrEscalated_SinceTheLastAcceptedOne()
    {
        var collector = new ErrorSignatureCollector();
        for (var i = 0; i < 5; i++)
            collector.Record("A", 0, LogLevel.Error, "flapping", null, null, T0.AddSeconds(i));

        var first = collector.Pending();
        first.Should().HaveCount(1);
        first[0].Count.Should().Be(5);

        collector.MarkReported(first);
        collector.Pending().Should().BeEmpty("reported at 5, and nothing happened since");

        for (var i = 0; i < 4; i++)
            collector.Record("A", 0, LogLevel.Error, "flapping", null, null, T0.AddSeconds(10 + i));
        collector.Pending().Should().BeEmpty("9 is not an escalation over 5");

        collector.Record("A", 0, LogLevel.Error, "flapping", null, null, T0.AddSeconds(20));
        var escalated = collector.Pending();
        escalated.Should().HaveCount(1, "the count doubled");
        escalated[0].Count.Should().Be(10);

        collector.MarkReported(escalated);
        for (var i = 0; i < 100; i++)
            collector.Record("A", 0, LogLevel.Error, "flapping", null, null, T0.AddSeconds(30 + i));
        collector.Pending().Single().Count.Should().Be(110, "a hundred more is an escalation before a doubling");
    }

    [Fact]
    public void ARefusedDigest_LeavesEverythingPending()
    {
        var collector = new ErrorSignatureCollector();
        collector.Record("A", 0, LogLevel.Error, "once", null, null, T0);

        // The reporter calls MarkReported only on acceptance; a refusal calls nothing.
        collector.Pending().Should().HaveCount(1);
        collector.Pending().Should().HaveCount(1, "reading pending is not reporting");
    }

    [Fact]
    public void TheSignature_IsStableAcrossProcesses_AndHashesTheTemplateNotTheLine()
    {
        var a = ErrorSignatureCollector.ComputeSignature("Cat", 7, "Failed {X}", "System.Exception", "MeshWeaver.A.B");
        var b = ErrorSignatureCollector.ComputeSignature("Cat", 7, "Failed {X}", "System.Exception", "MeshWeaver.A.B");
        var c = ErrorSignatureCollector.ComputeSignature("Cat", 8, "Failed {X}", "System.Exception", "MeshWeaver.A.B");

        a.Should().Be(b).And.MatchRegex("^[0-9a-f]{16}$");
        c.Should().NotBe(a);
    }

    [Fact]
    public void TheTopFrame_IsTheFirstApplicationFrame_WithoutArgumentsOrFile()
    {
        Exception? caught = null;
        try
        {
            Throwing.Site();
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        var frame = ErrorSignatureLoggerProvider.TopFrame(caught);
        frame.Should().NotBeNull();
        frame!.Should().StartWith("MeshWeaver.Hosting.Test.").And.Contain("Throwing").And.NotContain("(").And.NotContain(".cs");
        ErrorSignatureLoggerProvider.TopFrame(null).Should().BeNull();
        ErrorSignatureLoggerProvider.TopFrame(new InvalidOperationException("never thrown")).Should().BeNull();
    }

    [Fact]
    public void TheOwnReporterCategory_IsNotCollected_SoADeliveryFaultCannotFeedItself()
    {
        var (collector, logger) = Tap("MeshWeaver.PluginCatalog.InstanceErrorReporter");

        logger.LogError("the digest did not reach the registry");

        collector.Count.Should().Be(0);
    }

    [Fact]
    public void Bound_CapsSignaturesAndBytes_MostFrequentFirst_AndMasksEveryField()
    {
        var many = Enumerable.Range(0, ErrorDigestPayloads.MaxSignatures + 20)
            .Select(i => new ErrorSignature($"sig{i:D4}", "Cat", 0, "Error", $"template {i} token=abc{i}secret", null, null, i, T0, T0))
            .ToImmutableList();
        var request = new ErrorDigestPayloads.Request(new InstanceTelemetry("3.1.0"), T0, many);

        var bounded = ErrorDigestPayloads.Bound(request);

        bounded.Signatures.Should().HaveCount(ErrorDigestPayloads.MaxSignatures);
        bounded.Signatures[0].Count.Should().Be(many.Max(s => s.Count), "the most frequent come first");
        bounded.Signatures.Should().OnlyContain(s => !s.Template.Contains("secret"), "every text field is masked on the way out");
        ErrorDigestPayloads.SizeOf(bounded).Should().BeLessThanOrEqualTo(ErrorDigestPayloads.MaxBodyBytes);
        JsonSerializer.Serialize(ErrorDigestPayloads.Bound(bounded), ErrorDigestPayloads.Json)
            .Should().Be(JsonSerializer.Serialize(bounded, ErrorDigestPayloads.Json), "bounding is idempotent");

        var huge = new ErrorDigestPayloads.Request(null, T0, Enumerable.Range(0, ErrorDigestPayloads.MaxSignatures)
            .Select(i => new ErrorSignature($"sig{i:D4}", new string('c', 600), 0, "Error", new string('t', 900), new string('e', 600), new string('f', 600), 1, T0, T0))
            .ToImmutableList());
        var shrunk = ErrorDigestPayloads.Bound(huge);
        ErrorDigestPayloads.SizeOf(shrunk).Should().BeLessThanOrEqualTo(ErrorDigestPayloads.MaxBodyBytes);
        shrunk.Signatures[0].Template.Length.Should().Be(ErrorDigestPayloads.MaxTemplateLength);
        shrunk.Signatures[0].Category.Length.Should().Be(ErrorDigestPayloads.MaxFieldLength);
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnopqrstuvwxyz", "eyJ")]
    [InlineData("key mwi_0123456789abcdefghijklmnop presented", "mwi_0123")]
    [InlineData("GET https://store.example/files?sig=AbCdEf123456&x=1", "AbCdEf")]
    [InlineData("Password=SuperSecret1;Server=db", "SuperSecret1")]
    [InlineData("https://user:pw@host.example/path", "user:pw")]
    [InlineData("mail from max.muster@example.com bounced", "max.muster")]
    public void Mask_RemovesCredentialsAndPeople(string text, string mustNotSurvive)
    {
        var masked = TelemetryRedaction.Mask(text);

        masked.Should().NotContain(mustNotSurvive);
        TelemetryRedaction.Mask(masked).Should().Be(masked, "masking is idempotent");
    }

    private static class Throwing
    {
        public static void Site() => throw new InvalidOperationException("boom");
    }
}
