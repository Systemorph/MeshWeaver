using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.AI;
using MeshWeaver.Cli;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The HTTP contract of every string-returning <c>/api/mesh/*</c> verb (MeshWeaver.Plugins#1699):
/// <c>response.ok</c> means "here is the document", and nothing else.
///
/// <para><b>Why it exists.</b> <c>RunString</c> shipped whatever string a <see cref="MeshOperations"/>
/// verb produced as <c>200 application/json</c> — on the theory, written in its own comment, that
/// the sentinel was "a JSON-quoted value the client can branch on". It was never quoted: a bare
/// <c>Unavailable: …</c> is prose, so an Education e2e passed its <c>expect(response.ok())</c>
/// and died inside <c>JSON.parse</c> with nothing naming the path or the reason. This pins the
/// mapping at the seam where the result becomes an <see cref="IResult"/>, executed through a real
/// <see cref="HttpContext"/> so what is asserted is the status and the bytes a caller sees, not the
/// classifier's opinion of itself.</para>
///
/// <para><b>Both directions.</b> A document must still go out verbatim with a 200 — a mapping that
/// reddened every read would be the opposite defect — and the negative controls are the three
/// prefixes each landing on its own status, plus a document whose CONTENT mentions the words.</para>
/// </summary>
public class MeshApiSentinelStatusTest
{
    [Fact]
    public async Task A_json_document_ships_verbatim_with_200()
    {
        const string document = """{"path":"Doc/Guide","content":{"error":"Error: not a sentinel"}}""";

        var (status, body, contentType) = await ExecuteAsync(MeshApiEndpoints.Ship(document));

        status.Should().Be((int)HttpStatusCode.OK);
        contentType.Should().StartWith("application/json");
        body.Should().Be(document, "a document goes out byte-for-byte; the sentinel test is on the "
            + "PREFIX, so a document whose content carries the words is untouched");
    }

    [Theory]
    [InlineData("Not found: Doc/Nope", HttpStatusCode.NotFound, "NotFound")]
    [InlineData("Unavailable: Doc/Guide — this read reached no verdict, so it is UNKNOWN whether this node exists. Retry shortly. Cause: timeout", HttpStatusCode.ServiceUnavailable, "Unavailable")]
    [InlineData("Error: path is required.", HttpStatusCode.InternalServerError, "Error")]
    public async Task A_sentinel_ships_as_a_non_2xx_json_envelope(string sentence, HttpStatusCode expected, string kind)
    {
        var (status, body, contentType) = await ExecuteAsync(MeshApiEndpoints.Ship(sentence));

        status.Should().Be((int)expected);
        contentType.Should().StartWith("application/json");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().Should().Be(sentence,
            "the sentence names the path and the cause — the envelope must not lose it");
        doc.RootElement.GetProperty("kind").GetString().Should().Be(kind);
    }

    /// <summary>The MCP surface's contract is untouched: the classifier reads the prefix only.</summary>
    [Fact]
    public void The_classifier_reads_the_prefix_and_nothing_else()
    {
        OperationSentinel.Classify(null).Should().BeNull();
        OperationSentinel.Classify("").Should().BeNull();
        OperationSentinel.Classify("[]").Should().BeNull();
        OperationSentinel.Classify("\"Error: quoted\"").Should().BeNull("a JSON string literal is a document");
        OperationSentinel.Classify(" Error: leading space").Should().BeNull("the verbs never pad a sentinel");
        OperationSentinel.Classify("error: lower").Should().BeNull("ordinal, like the verbs that write it");
        OperationSentinel.Classify("Not found: x")!.Kind.Should().Be(SentinelKind.NotFound);
        OperationSentinel.Classify("Unavailable: x")!.Kind.Should().Be(SentinelKind.Unavailable);
        OperationSentinel.Classify("Error: x")!.Kind.Should().Be(SentinelKind.Error);
    }

    /// <summary>
    /// The CLI's stdout/stderr/exit contract survives the status change: it unwraps the envelope
    /// back to the sentence (so <c>Error:</c> still exits 1 with the sentence on stderr), and an
    /// envelope that is NOT a sentinel — an unmapped route's 404 — stays an HTTP failure.
    /// </summary>
    [Fact]
    public void The_cli_unwraps_the_envelope_and_only_the_envelope()
    {
        MemexClient.UnwrapSentinel("""{"error":"Not found: Doc/Nope","kind":"NotFound"}""")
            .Should().Be("Not found: Doc/Nope");
        MemexClient.UnwrapSentinel("""{"kind":"Error","error":"Error: path is required."}""")
            .Should().Be("Error: path is required.");
        MemexClient.UnwrapSentinel("""{"error":"No API endpoint at /api/mesh/nope"}""")
            .Should().BeNull("no kind — an unmapped route's 404 is an HTTP failure, not a verb answer");
        MemexClient.UnwrapSentinel("""{"error":"x","kind":"Timeout"}""")
            .Should().BeNull("a kind this client does not know is not a sentinel");
        MemexClient.UnwrapSentinel("Unavailable: raw prose").Should().BeNull("not JSON at all");
        MemexClient.UnwrapSentinel("[]").Should().BeNull();
    }

    /// <summary>Runs the result through a real HttpContext and reads back what a caller would see.</summary>
    private static async Task<(int Status, string Body, string? ContentType)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var stream = new MemoryStream();
        http.Response.Body = stream;

        await result.ExecuteAsync(http);

        return (http.Response.StatusCode, Encoding.UTF8.GetString(stream.ToArray()), http.Response.ContentType);
    }
}
