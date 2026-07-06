using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Speech;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The client-facing speech-to-text endpoint from
/// <see href="/Doc/Architecture/CentralizedSpeech">CentralizedSpeech</see>: <c>POST /api/speech/transcribe</c>.
///
/// <para>Every client — React Native, the Blazor composer, MAUI — posts audio HERE, behind the portal's
/// Bearer auth, and the portal forwards it to the (typically cluster-internal) Whisper container via
/// <see cref="ISpeechTranscriber"/>. The model host never faces clients directly — they only ever see this
/// endpoint. The contract mirrors whisper.cpp's <c>/inference</c> (and the RN client's
/// <c>SpeechTranscriptionClient</c>): multipart <c>{ file, language?, response_format? }</c> →
/// <c>{"text": "…", "language": "…"}</c>.</para>
/// </summary>
public static class SpeechEndpoints
{
    /// <summary>
    /// Maps <c>POST /api/speech/transcribe</c>. Same Bearer policy as the rest of the REST surface
    /// (<c>/api/mesh/*</c>); antiforgery is disabled because a Bearer-auth multipart post carries no
    /// antiforgery token (identical to <c>/api/mesh/upload</c>).
    /// </summary>
    public static IEndpointRouteBuilder MapSpeechApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/speech/transcribe", HandleTranscribe)
            .RequireAuthorization(McpAuthenticationExtensions.PolicyName)
            .DisableAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> HandleTranscribe(
        HttpContext http, ISpeechTranscriber transcriber, CancellationToken ct)
    {
        // Off/unset => tell the caller plainly rather than 500. The mic UI also checks IsConfigured
        // (via /api/mesh base-url / config) and stays hidden, so this is a belt-and-suspenders guard.
        if (!transcriber.IsConfigured)
            return Results.Json(
                new { error = "Speech transcription is not configured (no Whisper endpoint, or disabled)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "Content-Type must be multipart/form-data." });

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "Form file 'file' is required." });

        using var ms = new MemoryStream();
        await using (var stream = file.OpenReadStream())
            await stream.CopyToAsync(ms, ct);

        // Only override the transcriber's defaults when the multipart part actually carries the value —
        // SpeechTranscriptionOptions defaults (audio/wav, "de"/config language) must survive an omission.
        var options = new SpeechTranscriptionOptions
        {
            Language = form["language"].FirstOrDefault() is { Length: > 0 } lang ? lang : null,
        };
        if (!string.IsNullOrWhiteSpace(file.ContentType))
            options = options with { ContentType = file.ContentType };
        if (!string.IsNullOrWhiteSpace(file.FileName))
            options = options with { FileName = file.FileName };

        // Reactive transcriber → Task only at this HTTP boundary (sanctioned SDK-surface adapter — the
        // HTTP round-trip to Whisper runs on the HTTP IIoPool inside Transcribe). A transcriber fault is
        // surfaced to the caller as 502, never swallowed; a client abort propagates as cancellation.
        try
        {
            var transcript = await transcriber.Transcribe(ms.ToArray(), options).FirstAsync().ToTask(ct);
            return Results.Json(new { text = transcript.Text, language = transcript.Language });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Results.Json(
                new { error = $"Transcription failed: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
