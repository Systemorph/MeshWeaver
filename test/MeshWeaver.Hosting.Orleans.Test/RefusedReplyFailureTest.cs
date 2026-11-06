using System.Text.Json;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// #7842 — what the router says about a refused reply: the DIAGNOSTIC line identifies a PACKAGED
/// reply by its payload (never by the <see cref="RawJson"/> wrapper), and the sentence the asker
/// receives is the catalog's, in the asker's language.
/// </summary>
public class RefusedReplyFailureTest
{
    private static readonly Address Asker = new("silohub", "asker");
    private static readonly Address Replier = new("silohub", "replier");

    /// <summary>A reply as production routes it: correlated to a request, then packaged to RawJson.</summary>
    private static IMessageDelivery PackagedReply(string? locale)
    {
        var request = new MessageDelivery<PingRequest>(
            new PingRequest(), new PostOptions(Asker).WithTarget(Replier), JsonSerializerOptions.Default)
             with { AccessContext = new AccessContext { ObjectId = "someone", Locale = locale } };
        // The shape a hub's packaging produces: the payload erased to RawJson carrying its $type
        // discriminator (a hub packages with its own polymorphic options; the runtime defaults this
        // test has would write no discriminator at all).
        return new MessageDelivery<PingResponse>(
                new PingResponse(), new PostOptions(Replier).ResponseFor(request), JsonSerializerOptions.Default)
            .WithMessage(new RawJson($"{{\"$type\":\"{nameof(PingResponse)}\"}}"));
    }

    [Fact]
    public void ThePackagedReply_IsNamedByItsPayload_NotByTheWrapper()
    {
        var reply = PackagedReply(locale: null);
        reply.Message.Should().BeOfType<RawJson>(
            "the case must be the PACKAGED shape production routes — otherwise it proves nothing about the wrapper");

        var line = RoutingGrain.DescribeRefusedReply(reply, "no silo in this cluster is currently serving that hub");

        line.Should().Contain(DeliveryPayloadBounds.Quote(DeliveryPayloadBounds.Preview(reply)),
            "the payload is identified by the bounded reader, JSON-quoted as its contract requires");
        line.Should().Contain(nameof(PingResponse), "the reader names the reply's own $type");
        line.Should().NotContain(nameof(RawJson), "the wrapper's CLR type says nothing about which reply was lost");
    }

    [Fact]
    public void TheAskersSentence_IsTheCatalogsInTheAskersLanguage_AndCarriesNoDiagnostics()
    {
        var failure = RoutingGrain.RefusedReplyFailure(PackagedReply(locale: "de"));

        failure.Should().NotBeNull("a packaged correlated reply is still an awaited reply");
        var verdict = failure!.Message.Should().BeOfType<DeliveryFailure>().Subject;
        verdict.Message.Should().Be(LocalizationCatalog.Get(RoutingGrain.RefusedReplyMessageKey, "de"),
            "the sentence a person may read is resolved in the asker's language, which the reply carries");
        verdict.Message.Should().NotBe(LocalizationCatalog.Get(RoutingGrain.RefusedReplyMessageKey, "en"),
            "German must not fall back to English — otherwise the key is missing from the German catalog");
        verdict.Message.Should().NotContain("silo", "router diagnostics belong in the log, not in the viewer's sentence");
        verdict.ErrorType.Should().Be(ErrorType.ShuttingDown);
    }
}
