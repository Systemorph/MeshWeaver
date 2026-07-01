using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Speech.Test;

/// <summary>
/// Exercises <see cref="WhisperContainerTranscriber"/> end-to-end over a REAL in-process HTTP server that
/// mimics the <c>whisper.cpp</c> server's <c>POST /inference</c> contract — no mocks, no external container.
/// Proves the transcriber posts the audio as multipart, forwards the language, parses the JSON transcript,
/// and fails cleanly when unconfigured.
/// </summary>
public class WhisperContainerTranscriberTest
{
    private static WhisperContainerTranscriber Transcriber(HttpClient http, SpeechConfiguration cfg)
        => new(http, ioPoolRegistry: null, () => cfg, NullLogger<WhisperContainerTranscriber>.Instance);

    [Fact]
    public async Task Transcribes_audio_via_the_whisper_inference_endpoint()
    {
        string? seenPath = null, seenLanguage = null;
        long seenBytes = 0;
        using var server = new FakeWhisperServer(async ctx =>
        {
            seenPath = ctx.Request.Url?.AbsolutePath;
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            seenBytes = body.Length;
            seenLanguage = FieldValue(body, "language");
            return "{\"text\": \" Grüezi mitenand \"}"; // whisper.cpp json shape, with leading/trailing space
        });

        using var http = new HttpClient();
        var cfg = new SpeechConfiguration { Endpoint = server.BaseUrl, Enabled = true, Language = "de" };

        var result = await Transcriber(http, cfg).Transcribe(WavBytes()).FirstAsync().ToTask();

        result.Text.Should().Be("Grüezi mitenand"); // parsed from {"text":...} AND trimmed
        result.Language.Should().Be("de");
        seenPath.Should().Be("/inference");          // hit the whisper.cpp endpoint
        seenLanguage.Should().Be("de");              // language forwarded
        seenBytes.Should().BeGreaterThan(0);         // audio actually posted
    }

    [Fact]
    public async Task Per_call_language_option_overrides_the_configured_default()
    {
        string? seenLanguage = null;
        using var server = new FakeWhisperServer(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            seenLanguage = FieldValue(body, "language");
            return "{\"text\":\"bonjour\"}";
        });
        using var http = new HttpClient();
        var cfg = new SpeechConfiguration { Endpoint = server.BaseUrl, Enabled = true, Language = "de" };

        var result = await Transcriber(http, cfg)
            .Transcribe(WavBytes(), new SpeechTranscriptionOptions { Language = "fr" })
            .FirstAsync().ToTask();

        result.Text.Should().Be("bonjour");
        seenLanguage.Should().Be("fr"); // the option won over the configured "de"
    }

    [Fact]
    public async Task Not_configured_surfaces_a_clear_error_rather_than_calling_out()
    {
        using var http = new HttpClient();
        var cfg = new SpeechConfiguration { Enabled = false }; // no endpoint, disabled

        Transcriber(http, cfg).IsConfigured.Should().BeFalse();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Transcriber(http, cfg).Transcribe(WavBytes()).FirstAsync().ToTask());
        ex.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task Server_error_propagates_as_an_observable_error()
    {
        using var server = new FakeWhisperServer(_ => Task.FromResult("upstream failure"), status: 500);
        using var http = new HttpClient();
        var cfg = new SpeechConfiguration { Endpoint = server.BaseUrl, Enabled = true };

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            async () => await Transcriber(http, cfg).Transcribe(WavBytes()).FirstAsync().ToTask());
    }

    private static byte[] WavBytes() => Encoding.ASCII.GetBytes("RIFF....WAVEfmt ...."); // stand-in payload

    /// <summary>Extract a multipart form-data field's value from a raw body, tolerant of quoted/unquoted
    /// <c>name=</c> and any part headers (e.g. StringContent's Content-Type) before the blank line.</summary>
    private static string? FieldValue(string body, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            body, "name=\"?" + name + "\"?\r\n(?:[^\r\n]+\r\n)*\r\n(?<v>[^\r\n]*)");
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>A throwaway HttpListener that answers one route the way whisper.cpp's server would.</summary>
    private sealed class FakeWhisperServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }

        public FakeWhisperServer(Func<HttpListenerContext, Task<string>> handler, int status = 200)
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; } // listener stopped
                    try
                    {
                        var body = await handler(ctx);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    catch
                    {
                        ctx.Response.StatusCode = status == 200 ? 500 : status;
                    }
                    finally { ctx.Response.Close(); }
                }
            });
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
            ((IDisposable)_listener).Dispose();
        }
    }
}
