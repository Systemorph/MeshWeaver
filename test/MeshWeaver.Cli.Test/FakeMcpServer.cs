using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// A loopback MCP endpoint for the CLI tests: <c>POST /mcp</c> answers <c>initialize</c>,
/// <c>tools/list</c> and <c>tools/call</c> as JSON-RPC (plain JSON, or an event stream when
/// <see cref="ReplyAsEventStream"/> is set), and <c>POST /api/mesh/upload</c> stands in for the REST
/// upload route. Every request is recorded. A fake SERVER, not a mock of a MeshWeaver interface: the
/// CLI's boundary is the wire protocol, and the wire is what is exercised.
///
/// <para>Kestrel bound to port 0 — the same choice and reason as <c>TestOgServer</c> (#2436): the port
/// is never unowned between discovery and use.</para>
/// </summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    public const string Token = "mw_test";

    private WebApplication app = null!;

    /// <summary>The <c>tools/list</c> answer.</summary>
    public JsonArray Tools { get; set; } = [];

    /// <summary>Reply in <c>text/event-stream</c> framing instead of a JSON body.</summary>
    public volatile bool ReplyAsEventStream;

    /// <summary>With <see cref="ReplyAsEventStream"/>: after the reply, keep the stream OPEN until the
    /// client goes away — a server that answers and lingers.</summary>
    public volatile bool HoldEventStreamOpen;

    private int heldStreams;

    /// <summary>Answer <c>tools/list</c> with a result that has no <c>tools</c> array.</summary>
    public volatile bool MalformedToolsList;

    /// <summary>Open an event stream for <c>tools/call</c> and never send the reply.</summary>
    public volatile bool NeverAnswerCalls;

    /// <summary>How many replies were followed by a held-open stream.</summary>
    public int HeldStreams => Volatile.Read(ref heldStreams);

    /// <summary>Every JSON-RPC method received, with its params, in arrival order.</summary>
    public ConcurrentQueue<(string Method, JsonNode? Params)> Received { get; } = new();

    /// <summary>Every REST upload received (the form's <c>path</c> field).</summary>
    public ConcurrentQueue<string> Uploads { get; } = new();

    /// <summary>Calls received, as (tool, arguments).</summary>
    public ImmutableList<(string Name, JsonObject Arguments)> Calls =>
        Received.Where(r => r.Method == "tools/call")
            .Select(r => (r.Params!["name"]!.GetValue<string>(), (JsonObject)r.Params!["arguments"]!))
            .ToImmutableList();

    public int ListRequests => Received.Count(r => r.Method == "tools/list");

    public string BaseUrl { get; private set; } = string.Empty;

    private FakeMcpServer()
    {
    }

    public static async Task<FakeMcpServer> StartAsync(JsonArray tools)
    {
        var server = new FakeMcpServer { Tools = tools };
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(server.ServeAsync);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.app = app;
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (context.Request.Headers.Authorization.ToString() != "Bearer " + Token)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (context.Request.Path == "/api/mesh/upload")
        {
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            Uploads.Enqueue(form["path"].ToString());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"Uploaded\"}").ConfigureAwait(false);
            return;
        }

        if (context.Request.Path != "/mcp")
        {
            context.Response.StatusCode = 404;
            return;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var message = JsonNode.Parse(await reader.ReadToEndAsync().ConfigureAwait(false))!.AsObject();
        var method = message["method"]!.GetValue<string>();
        Received.Enqueue((method, message["params"]?.DeepClone()));

        if (message["id"] is null)
        {
            context.Response.StatusCode = 202; // a notification has no reply
            return;
        }

        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone() };
        switch (method)
        {
            case "initialize":
                reply["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1" },
                };
                break;
            case "tools/list":
                reply["result"] = MalformedToolsList ? new JsonObject() : new JsonObject { ["tools"] = Tools.DeepClone() };
                break;
            case "tools/call" when NeverAnswerCalls:
                // An event stream that opens, carries an unrelated event, and never the reply.
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n").ConfigureAwait(false);
                await context.Response.Body.FlushAsync().ConfigureAwait(false);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return;
            case "tools/call":
                var name = message["params"]!["name"]!.GetValue<string>();
                if (!Tools.Any(t => t!["name"]!.GetValue<string>() == name))
                    reply["error"] = new JsonObject { ["code"] = -32602, ["message"] = $"Unknown tool: '{name}'" };
                else if (name == "wrong_id")
                    reply["id"] = 999_999; // a reply that answers some other request
                else
                    reply["result"] = Answer(name, (JsonObject)message["params"]!["arguments"]!);
                break;
            default:
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
                break;
        }

        if (ReplyAsEventStream)
        {
            context.Response.ContentType = "text/event-stream";
            // Counted BEFORE the reply is written: a client that has the reply has the count too.
            if (HoldEventStreamOpen) Interlocked.Increment(ref heldStreams);
            await context.Response.WriteAsync($"event: message\ndata: {reply.ToJsonString()}\n\n").ConfigureAwait(false);
            if (HoldEventStreamOpen)
            {
                await context.Response.Body.FlushAsync().ConfigureAwait(false);
                try
                {
                    // Not a wait for propagation: the hold IS the subject — the reply is out, the
                    // stream stays open until the client disconnects (or the server stops).
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        else
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(reply.ToJsonString()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What each tool answers: <c>fails</c> is a protocol-level tool error (<c>isError</c>),
    /// <c>sentinel</c> answers the mesh's <c>Error:</c> sentence, everything else echoes its arguments.
    /// </summary>
    private static JsonObject Answer(string name, JsonObject arguments) => name switch
    {
        "fails" => Text("the tool failed on purpose", isError: true),
        "sentinel" => Text("Error: Not permitted at Doc/X"),
        "empty_envelope" => new JsonObject(),
        "content_not_array" => new JsonObject { ["content"] = 42 },
        "empty_content" => new JsonObject { ["content"] = new JsonArray() },
        // A content item whose `type` is a number, not a string: a malformed result.
        "malformed" => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = 5, ["text"] = "x" }) },
        _ => Text(new JsonObject { ["tool"] = name, ["arguments"] = arguments.DeepClone() }.ToJsonString()),
    };

    private static JsonObject Text(string text, bool isError = false)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError) result["isError"] = true;
        return result;
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync().ConfigureAwait(false);
}
