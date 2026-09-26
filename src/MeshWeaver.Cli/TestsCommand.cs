using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshWeaver.Cli;

/// <summary>
/// <c>memex tests &lt;path&gt;</c> — run (= render) one node's <c>Tests</c> area and print its
/// progress as a console, line by line, until the verdict frame arrives.
///
/// <para>The Tests area IS the runner (<c>MeshTestRunner</c>): rendering it executes the cases, and
/// the area streams a progress frame per second — every case pending, running (with its elapsed
/// time and its output so far) or finished — then ONE verdict frame. This command reads the area
/// through the same <c>get @path/area/Tests</c> verb an agent uses, prints every row whose state or
/// output changed since the last read, and exits on the verdict: <c>0</c> when every counted case
/// passed, <c>1</c> when any failed, <c>4</c> when no verdict arrived within <c>--timeout</c> (the
/// last progress is printed, so a hung case is NAMED rather than waited on).</para>
///
/// <para>A Tests area that predates the streaming runner renders only its verdict, so the first read
/// simply prints the finished table — the command works against every portal, streaming or not.</para>
/// </summary>
public static class TestsCommand
{
    /// <summary>The <c>UiControl.Id</c> every progress frame carries (<c>AreaFrameClassifier.TestsRunningId</c>).</summary>
    public const string TestsRunningId = "tests-running";

    private static readonly Regex PassSummary = new(@"(\d+)\s*/\s*(\d+)\s+passed", RegexOptions.CultureInvariant);

    /// <summary>One case as the area renders it.</summary>
    /// <param name="Case">The case's name.</param>
    /// <param name="Result">Its status glyph / verdict.</param>
    /// <param name="Time">Its elapsed time.</param>
    /// <param name="Output">Its failure message and output lines.</param>
    public sealed record Row(string Case, string Result, string Time, string Output);

    /// <summary>What one read of the area says.</summary>
    /// <param name="Running">True for a progress frame — the verdict is still to come.</param>
    /// <param name="Title">The frame's title (progress or verdict), when it has one.</param>
    /// <param name="Rows">The cases the frame lists as grid rows (empty for a legacy markdown table).</param>
    /// <param name="Text">Every other string the Tests area carries (a legacy table's markdown).</param>
    public sealed record Frame(bool Running, string? Title, IReadOnlyList<Row> Rows, IReadOnlyList<string> Text)
    {
        /// <summary>True when the verdict frame says every counted case passed.</summary>
        public bool Passed
        {
            get
            {
                var all = Rows.Select(r => r.Result).Concat(Text).Append(Title ?? "");
                if (all.Any(s => s.Contains('❌')))
                    return false;
                var summary = all.Select(s => PassSummary.Match(s)).FirstOrDefault(m => m.Success);
                return summary is null
                    ? Rows.Count > 0 || all.Any(s => s.Contains('✅'))
                    : summary.Groups[1].Value == summary.Groups[2].Value;
            }
        }
    }

    /// <summary>
    /// Reads a <c>get @path/area/Tests</c> answer. Pure over the JSON: the chrome areas the framework
    /// writes into every subscription (<c>$Menu…</c>, <c>$Banner</c>) are skipped, exactly as the
    /// plugin gate skips them.
    /// </summary>
    /// <param name="json">The rendered area store.</param>
    public static Frame Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<Row>();
        var text = new List<string>();
        string? title = null;
        var running = false;
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("areas", out var areas)
            && areas.ValueKind == JsonValueKind.Object)
        {
            foreach (var area in areas.EnumerateObject())
            {
                if (area.Name.TrimStart('"').StartsWith('$'))
                    continue;
                Walk(area.Value);
            }
        }
        return new Frame(running, title, rows, text);

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && id.GetString() == TestsRunningId)
                        running = true;
                    if (e.TryGetProperty("case", out var c) && e.TryGetProperty("result", out var r))
                    {
                        rows.Add(new Row(Str(c), Str(r), Str(e, "time"), Str(e, "output")));
                        return;
                    }
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        Walk(item);
                    break;
                case JsonValueKind.String:
                    var s = e.GetString() ?? "";
                    if (title is null && (s.Contains(" tests — ", StringComparison.Ordinal) || s.Contains("-Tests — ", StringComparison.Ordinal)))
                        title = StripTags(s);
                    else if (s.Contains('✅') || s.Contains('❌') || PassSummary.IsMatch(s))
                        text.Add(s);
                    break;
            }
        }
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();

    private static string Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) ? Str(v) : "";

    private static string StripTags(string s) => Regex.Replace(s, "<[^>]+>", "").Trim();

    /// <summary>
    /// The lines to print for <paramref name="next"/> given what was already printed: a row that is
    /// new, or whose result or output changed. A row whose only change is its ticking time is not
    /// reprinted — the console would scroll with nothing new in it.
    /// </summary>
    /// <param name="printed">The rows as last printed, by case name.</param>
    /// <param name="next">The frame just read.</param>
    public static IReadOnlyList<string> Changes(IDictionary<string, Row> printed, Frame next)
    {
        var lines = new List<string>();
        foreach (var row in next.Rows)
        {
            if (printed.TryGetValue(row.Case, out var before)
                && before.Result == row.Result && before.Output == row.Output)
                continue;
            printed[row.Case] = row;
            lines.Add($"{row.Result,-10} {row.Case}{(row.Time.Length > 0 ? $" ({row.Time})" : "")}{(row.Output.Length > 0 ? $" — {row.Output}" : "")}");
        }
        return lines;
    }

    /// <summary>Reads the area until the verdict, printing each change; returns the exit code.</summary>
    /// <param name="client">The portal client.</param>
    /// <param name="path">The node whose Tests area to run.</param>
    /// <param name="timeout">How long to wait for the verdict.</param>
    /// <param name="interval">How often to read the area.</param>
    /// <param name="output">Where the console goes.</param>
    /// <param name="ct">Cancels the wait.</param>
    public static async Task<int> Run(MemexClient client, string path, TimeSpan timeout, TimeSpan interval, TextWriter output, CancellationToken ct)
    {
        var area = $"@{path.TrimStart('@').TrimEnd('/')}/area/Tests";
        var printed = new Dictionary<string, Row>(StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastTitle = null;
        while (true)
        {
            var body = await client.Get(area, ct);
            if (body.StartsWith("Error:", StringComparison.Ordinal) || body.StartsWith("Not found", StringComparison.Ordinal))
            {
                await output.WriteLineAsync(body);
                return 1;
            }
            var frame = Parse(body);
            if (frame.Title is { } title && title != lastTitle)
            {
                await output.WriteLineAsync($"== {title}");
                lastTitle = title;
            }
            foreach (var line in Changes(printed, frame))
                await output.WriteLineAsync(line);
            if (!frame.Running)
            {
                foreach (var line in frame.Text)
                    await output.WriteLineAsync(line);
                return frame.Passed ? 0 : 1;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                var stuck = printed.Values.Where(r => r.Result == "▶").Select(r => r.Case).ToList();
                await output.WriteLineAsync(
                    $"no verdict within {timeout.TotalSeconds:F0}s — still running: {(stuck.Count > 0 ? string.Join(", ", stuck) : "(no case reported running)")}");
                return 4;
            }
            await Task.Delay(interval, ct);
        }
    }
}
