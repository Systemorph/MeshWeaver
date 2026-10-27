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
    public void AnInterpolatedMessage_HasNoNamedPlaceholder_AndIsNormalisedLikeAPreFormattedOne()
    {
        var (collector, logger) = Tap();
        var guid = Guid.NewGuid();

        // The standard overload with an interpolated string: FormattedLogValues still carries an
        // {OriginalFormat} — the already-interpolated text — and no named property.
        logger.LogError($"Node {guid} failed for jane.roe@example.com at /data/Store/Orders/42");
        logger.LogError($"Node {Guid.NewGuid()} failed for max.muster@example.com at /data/Store/Orders/43");

        var pending = collector.Pending();
        pending.Should().HaveCount(1, "the two interpolated lines normalise to ONE signature");
        pending[0].Template.Should().NotContain(guid.ToString()).And.NotContain("jane.roe").And.NotContain("Orders/42");
        pending[0].Template.Should().Contain("{guid}").And.Contain(TelemetryRedaction.MaskedEmail).And.Contain("{path}");
    }

    [Fact]
    public void AQuotedCredentialAcrossTheClipBoundary_IsMaskedBeforeItIsClipped()
    {
        var collector = new ErrorSignatureCollector();
        var filler = new string('x', ErrorDigestPayloads.MaxTemplateLength - 20);
        var secret = "SuperSecretValueThatCrossesTheBoundary";

        collector.Record("A", 0, LogLevel.Error, $"{filler} Password=\"{secret}\" refused", null, null, T0);

        // Collector to digest: what the reporter would POST, bounded exactly as it leaves the process.
        var digest = ErrorDigestPayloads.Bound(new ErrorDigestPayloads.Request(new InstanceTelemetry("3.1.0"), T0, collector.Pending()));
        var wire = JsonSerializer.Serialize(digest, ErrorDigestPayloads.Json);
        wire.Should().NotContain("SuperSecret").And.NotContain(secret[..10], "masked whole BEFORE the clip, not clipped mid-quote");
        digest.Signatures.Single().Template.Should().Contain(TelemetryRedaction.Masked)
            .And.HaveLength(ErrorDigestPayloads.MaxTemplateLength, "the clip still applies, to the masked text");
    }

    [Theory]
    [InlineData("Import of alice/Private/Invoices failed", "alice")]
    [InlineData(@"Cannot open C:\Users\alice\Private\Invoices.xlsx", "alice")]
    [InlineData("Node acme/Crm/Contacts/4711 refused", "acme")]
    public void Normalize_ReplacesRelativeMeshAndWindowsPaths(string text, string mustNotSurvive)
    {
        var normalized = TelemetryRedaction.Normalize(text);

        normalized.Should().NotContain(mustNotSurvive).And.Contain("{path}");
    }

    [Theory]
    [InlineData("Import of alice/Private/Invoices failed", "alice")]
    [InlineData(@"Cannot open C:\Users\alice\Private\Invoices.xlsx", "alice")]
    public void APrivatePath_LoggedWithoutATemplate_ReachesTheDigestWithoutAUserName(string message, string mustNotSurvive)
    {
        var (collector, logger) = Tap();

        // Parameter-free: the state is a plain string, so the collector normalises the formatted text.
        logger.Log(LogLevel.Error, 0, message, null, (state, _) => state);

        var digest = ErrorDigestPayloads.Bound(new ErrorDigestPayloads.Request(new InstanceTelemetry("3.1.0"), T0, collector.Pending()));
        var wire = JsonSerializer.Serialize(digest, ErrorDigestPayloads.Json);
        wire.Should().NotContain(mustNotSurvive).And.NotContain("Private").And.NotContain("Invoices",
            "a relative mesh path or a Windows path names a person or a machine; the digest carries {path}");
        digest.Signatures.Single().Template.Should().Contain("{path}");
    }

    [Theory]
    [InlineData("""token exchange refused: {"client_secret":"SuperSecretValue"}""")]
    [InlineData("callback carried access_token=SuperSecretValue&scope=x")]
    [InlineData("refresh_token: SuperSecretValue rejected")]
    public void AnOAuthCredential_LoggedWithoutATemplate_ReachesTheDigestMasked(string message)
    {
        var (collector, logger) = Tap();

        logger.Log(LogLevel.Error, 0, message, null, (state, _) => state);

        var digest = ErrorDigestPayloads.Bound(new ErrorDigestPayloads.Request(new InstanceTelemetry("3.1.0"), T0, collector.Pending()));
        var wire = JsonSerializer.Serialize(digest, ErrorDigestPayloads.Json);
        wire.Should().NotContain("SuperSecretValue", "an OAuth field name is a credential key like any other");
        digest.Signatures.Single().Template.Should().Contain(TelemetryRedaction.Masked);
    }

    [Fact]
    public void TheBound_HoldsUnderConcurrentRecords()
    {
        var collector = new ErrorSignatureCollector(capacity: 8);

        System.Threading.Tasks.Parallel.For(0, 2000, i =>
            collector.Record("A", 0, LogLevel.Error, $"distinct {i}", null, null, T0.AddTicks(i)));

        collector.Count.Should().BeLessThanOrEqualTo(8, "admission is a CAS on the slot counter, never a read of Count");
        (collector.Count + collector.Dropped).Should().Be(2000);
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
    [InlineData("token='SuperSecret1' refused", "SuperSecret1")]
    [InlineData("Password=\"SuperSecret1\";Server=db", "SuperSecret1")]
    [InlineData("GET https://store.example/files?sig=\"SuperSecret1\"&x=1", "SuperSecret1")]
    [InlineData("""body {"password":"SuperSecret1","user":"max"}""", "SuperSecret1")]
    [InlineData("""{"apiKey": "SuperSecret1"}""", "SuperSecret1")]
    [InlineData("""{"client_secret":"SuperSecretValue"}""", "SuperSecretValue")]
    [InlineData("access_token=SuperSecretValue&scope=x", "SuperSecretValue")]
    [InlineData("refresh_token: SuperSecretValue", "SuperSecretValue")]
    [InlineData("""{"id_token":"SuperSecretValue","AccountKey":"SuperSecretValue2"}""", "SuperSecretValue")]
    [InlineData("""Password="SuperSecretValueCutOff""", "SuperSecretValueCutOff")]
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
