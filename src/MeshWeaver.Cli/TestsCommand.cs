using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.Cli;

/// <summary>
/// <c>memex tests &lt;path&gt;</c> — run one node's <c>Tests</c> area and print its progress as a
/// console until the verdict.
///
/// <para>🚨 It does NOT poll <c>get @path/area/Tests</c>. Rendering the area RUNS the suite, and every
/// one-shot read opens a fresh subscription — measured on memex.meshweaver.cloud, each read filed the
/// Maintenance suite's live request nodes again, and answered with whatever verdict an earlier
/// subscription had cached. Instead the portal runs the area ONCE, holding one subscription, as an
/// activity (<c>POST api/mesh/run-tests</c> → <c>MeshOperations.RunTests</c>) that logs a line per
/// case as it starts, writes output and lands; this command polls that ACTIVITY node, which starts
/// nothing, and prints each new line.</para>
///
/// <para>Exit codes: <c>0</c> the activity Succeeded (every counted case passed), <c>1</c> it Failed
/// (a case failed, the area has none, or the portal's own bound elapsed — its last line names the
/// case still running), <c>4</c> no terminal status within <c>--timeout</c>.</para>
/// </summary>
public static class TestsCommand
{
    /// <summary>One read of the activity node.</summary>
    /// <param name="Status">The activity status (<c>Running</c>, <c>Succeeded</c>, <c>Failed</c>, …).</param>
    /// <param name="MessageCount">The true number of lines ever written.</param>
    /// <param name="Window">The most recent lines (older ones are archived under <c>{activity}/_Log</c>).</param>
    public sealed record ActivityRead(string Status, int MessageCount, ImmutableArray<string> Window)
    {
        /// <summary>True once the run has a terminal status.</summary>
        public bool Terminal => Status is not ("Running" or "Pending" or "");
    }

    /// <summary>Reads a <c>get @activity</c> answer. Pure over the JSON.</summary>
    /// <param name="json">The activity node.</param>
    public static ActivityRead ParseActivity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.TryGetProperty("content", out var c) ? c : doc.RootElement;
        var status = content.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        var messages = content.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var text) ? text.GetString() ?? "" : "")
                .ToImmutableArray()
            : [];
        var count = content.TryGetProperty("messageCount", out var n) && n.TryGetInt32(out var value) ? value : messages.Length;
        return new ActivityRead(status, count, messages);
    }

    /// <summary>
    /// The lines of <paramref name="read"/> not yet printed, given that <paramref name="printed"/>
    /// lines were printed before — plus a note when some slid out of the window unseen.
    /// </summary>
    /// <param name="printed">How many lines have been printed so far.</param>
    /// <param name="read">The activity just read.</param>
    public static ImmutableArray<string> NewLines(int printed, ActivityRead read)
    {
        var fresh = read.MessageCount - printed;
        if (fresh <= 0)
            return [];
        var shown = Math.Min(fresh, read.Window.Length);
        var lines = read.Window.Skip(read.Window.Length - shown);
        return fresh > shown
            ? [$"… {fresh - shown} line(s) were archived before they could be read (see the activity's _Log)", .. lines]
            : [.. lines];
    }

    /// <summary>Starts the run, then prints the activity's lines until it is terminal; returns the exit code.</summary>
    /// <param name="client">The portal client.</param>
    /// <param name="path">The node whose Tests area to run.</param>
    /// <param name="timeout">How long to wait for a terminal status.</param>
    /// <param name="interval">How often to read the activity.</param>
    /// <param name="output">Where the console goes.</param>
    /// <param name="ct">Cancels the wait.</param>
    public static async Task<int> Run(MemexClient client, string path, TimeSpan timeout, TimeSpan interval, TextWriter output, CancellationToken ct)
    {
        // The command's --timeout bounds EVERY request too, so a hung read cannot outlive it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var started = await client.RunTests(path, (int)Math.Ceiling(timeout.TotalSeconds), deadline.Token);
            using var dispatched = JsonDocument.Parse(started);
            if (dispatched.RootElement.TryGetProperty("status", out var s) && s.GetString() != "Dispatched"
                || !dispatched.RootElement.TryGetProperty("activityPath", out var activity))
            {
                await output.WriteLineAsync(started);
                return 1;
            }
            var activityPath = activity.GetString()!;
            await output.WriteLineAsync($"== {activityPath}");
            var printed = 0;
            while (true)
            {
                var read = ParseActivity(await client.Get("@" + activityPath, deadline.Token));
                foreach (var line in NewLines(printed, read))
                    await output.WriteLineAsync(line);
                printed = Math.Max(printed, read.MessageCount);
                if (read.Terminal)
                {
                    await output.WriteLineAsync($"== {read.Status}");
                    return read.Status == "Succeeded" ? 0 : 1;
                }
                await Task.Delay(interval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await output.WriteLineAsync($"no terminal status within {timeout.TotalSeconds:F0}s");
            return 4;
        }
    }
}
