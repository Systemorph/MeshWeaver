using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Compile-guard for the <c>LinkedInTelemetryImport</c> Code piece that lives
/// under <c>Systemorph/LinkedInProfile/Source/</c> in the production mesh.
///
/// Regression for 2026-04-23 incident: the Code piece shipped a broken
/// <c>string.Join</c> call that hit the new .NET 9 params-Span overload
/// ambiguity — the entire LinkedInProfile NodeType stopped compiling and the
/// dashboard rendered the raw compiler diagnostic. The existing
/// <c>LinkedInProfileLayoutAreaTest</c> only covered the simpler stub source
/// so it stayed green while prod broke.
///
/// The body inlined in <see cref="LinkedInTelemetryImportSource"/> below is the
/// authoritative production source — keep it in lockstep with the Code piece.
/// </summary>
public class LinkedInTelemetryImportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypePath = "Systemorph/LinkedInProfile";
    private const string SourceNamespace = "Systemorph/LinkedInProfile/Source";

    [Fact(Timeout = 60000)]
    public async Task LinkedInTelemetryImport_CompilesAndRendersImportArea()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;

        await NodeFactory.CreateNodeAsync(new MeshNode("LinkedInProfile", "Systemorph")
        {
            Name = "LinkedIn Profile",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "A user's linked LinkedIn profile.",
                Configuration =
                    "config => config.WithContentType<LinkedInProfile>()" +
                    ".AddDefaultLayoutAreas()" +
                    ".AddLayout(layout => layout.WithView(\"ImportTelemetry\", LinkedInTelemetryImport.ImportTelemetry))",
                ShowChildrenInDetails = false,
            }
        }, ct);

        await CreateCodeAsync("LinkedInProfile", LinkedInProfileSource, ct);
        await CreateCodeAsync("LinkedInTelemetryImport", LinkedInTelemetryImportSource, ct);

        var instancePath = $"{NodeTypePath}/test-profile";
        await NodeFactory.CreateNodeAsync(new MeshNode("test-profile", NodeTypePath)
        {
            Name = "Test",
            NodeType = NodeTypePath,
            Content = new Dictionary<string, object?>
            {
                ["$type"] = "LinkedInProfile",
                ["displayName"] = "Test",
                ["connectedAt"] = DateTimeOffset.UtcNow,
            }
        }, ct);

        // The NodeType must compile cleanly — render the Import area to trigger
        // the compile, then assert we got back a Stack containing the form.
        var control = await RenderAreaAsync(instancePath, "ImportTelemetry", ct);

        control.Should().NotBeNull();
        control.Should().BeOfType<StackControl>("ImportTelemetry composes instructions + textarea + button + result via Controls.Stack");

        var stack = (StackControl)control;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(3,
            "Import area should at least contain the instructions, the textarea, and the import button");
    }

    private Task CreateCodeAsync(string id, string source, CancellationToken ct) =>
        NodeFactory.CreateNodeAsync(new MeshNode(id, SourceNamespace)
        {
            Name = id,
            NodeType = "Code",
            Content = new CodeConfiguration { Code = source, Language = "csharp" }
        }, ct);

    private async Task<UiControl> RenderAreaAsync(string path, string area, CancellationToken ct)
    {
        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(area);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(30.Seconds())
            .FirstAsync(x => x is StackControl or HtmlControl or MarkdownControl)
            .ToTask(ct);

        control.Should().NotBeNull("ImportTelemetry must emit a control before the timeout");
        return (UiControl)control!;
    }

    // ---------- Production source (keep in lockstep with the Code piece in prod) ----------

    private const string LinkedInProfileSource = """
        using MeshWeaver.Domain;

        public record LinkedInProfile
        {
            [Required]
            [MeshNodeProperty(nameof(MeshNode.Name))]
            public string DisplayName { get; init; } = string.Empty;

            public string? SubjectUrn { get; init; }
            public string? ProfileUrl { get; init; }
            public string? PictureUrl { get; init; }
            public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
            public DateTimeOffset? LastSyncAt { get; init; }
        }
        """;

    /// <summary>
    /// Mirror of <c>Systemorph/LinkedInProfile/Source/LinkedInTelemetryImport</c>.
    /// Notable fixes vs the broken 2026-04-23 version:
    ///   - <c>string.Join(" | ", (IEnumerable&lt;string&gt;)header)</c>
    ///     disambiguates the new .NET 9 params-<c>ReadOnlySpan</c> overloads.
    ///   - The click handler returns <c>Task.CompletedTask</c> synchronously and
    ///     uses <c>Subscribe</c> + <c>Observable.FromAsync</c> for the parse work
    ///     (no <c>await</c> on hub-backed paths, per AsynchronousCalls.md).
    /// </summary>
    private const string LinkedInTelemetryImportSource = """
        using System.Reactive.Linq;
        using System.Text.Json;
        using System.Text.RegularExpressions;
        using System.Threading.Tasks;
        using MeshWeaver.Data;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;

        public static class LinkedInTelemetryImport
        {
            public static IObservable<UiControl?> ImportTelemetry(LayoutAreaHost host, RenderingContext _)
            {
                var hubPath = host.Hub.Address.ToString();
                var mesh = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

                const string csvDataId = "linkedinTelemetryCsv";
                const string resultDataId = "linkedinTelemetryResult";

                host.UpdateData(csvDataId, "");
                host.UpdateData(resultDataId, "");

                var instructions = Controls.Markdown(
                    "## Import LinkedIn analytics (CSV)\n\n" +
                    "1. Open your post analytics on LinkedIn (linkedin.com/in/<vanity>/analytics/post-summary/)\n" +
                    "2. Click Export, save as CSV UTF-8.\n" +
                    "3. Paste the CSV below and click Import.");

                var textarea = new TextAreaControl(new JsonPointerReference(""))
                    .WithPlaceholder("Paste the LinkedIn analytics CSV here...")
                    .WithRows(12)
                    .WithImmediate(true) with
                { DataContext = LayoutAreaReference.GetDataPointer(csvDataId) };

                var importBtn = Controls.Button("Import")
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(resultDataId, "### Starting...");
                        ctx.Host.Stream.GetDataStream<string>(csvDataId)
                            .Take(1)
                            .Subscribe(csv =>
                            {
                                if (string.IsNullOrWhiteSpace(csv))
                                {
                                    ctx.Host.UpdateData(resultDataId, "### Nothing to import\n\nPaste a CSV first.");
                                    return;
                                }
                                // Progress callback updates the result area as ImportAsync iterates rows.
                                void Progress(string msg) => ctx.Host.UpdateData(resultDataId, msg);
                                Observable.FromAsync(() => ImportAsync(mesh, hubPath, csv, Progress))
                                    .Subscribe(
                                        report => ctx.Host.UpdateData(resultDataId, report),
                                        ex => ctx.Host.UpdateData(resultDataId, "### Error\n\n" + ex.Message));
                            });
                        return Task.CompletedTask;
                    });

                var resultPlaceholder = Controls.Markdown("") with
                { DataContext = LayoutAreaReference.GetDataPointer(resultDataId) };

                var stack = Controls.Stack
                    .WithStyle("padding: 16px; gap: 16px;")
                    .WithView(instructions)
                    .WithView(textarea)
                    .WithView(importBtn)
                    .WithView(resultPlaceholder);

                return Observable.Return((UiControl?)stack);
            }

            public static async Task<string> ImportAsync(IMeshService mesh, string hubPath, string csv, Action<string>? progress = null)
            {
                progress?.Invoke("### Parsing CSV...");
                var rows = ParseCsv(csv);
                if (rows.Count < 2)
                    return "### Empty CSV\n\nPaste the full export including the header row.";

                var header = rows[0];
                var cols = MapColumns(header);
                if (cols.UrnIdx < 0 && cols.UrlIdx < 0)
                {
                    var headerStr = string.Join(" | ", (IEnumerable<string>)header);
                    return "### Couldn't find post identifier column\n\nDetected headers: `" + headerStr + "`. Need a column with `URL`, `URN`, or `Permalink`.";
                }

                progress?.Invoke("### Indexing existing posts...");
                var postsByUrn = new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
                await foreach (var p in mesh.QueryAsync<MeshNode>("namespace:" + hubPath + "/posts nodeType:Systemorph/Post"))
                {
                    var urn = TryGetString(p, "platformUrn");
                    if (!string.IsNullOrEmpty(urn)) postsByUrn[urn!] = p;
                }

                int imported = 0, unmatched = 0, skipped = 0;
                var importDate = DateTimeOffset.UtcNow;
                var sampleId = "t-" + importDate.ToString("yyyyMMddTHHmmssZ");
                var totalDataRows = rows.Count - 1;

                for (int i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace)) continue;

                    var urn = ExtractUrn(row, cols);
                    if (string.IsNullOrEmpty(urn)) { unmatched++; goto reportProgress; }
                    if (!postsByUrn.TryGetValue(urn!, out var postNode)) { unmatched++; goto reportProgress; }

                    var samplePath = postNode.Path + "/" + sampleId;
                    if (await Exists(mesh, samplePath)) { skipped++; goto reportProgress; }

                    var node = new MeshNode(sampleId, postNode.Path)
                    {
                        Name = importDate.ToString("yyyy-MM-dd") + " (CSV import)",
                        NodeType = "Systemorph/PostTelemetry",
                        State = MeshNodeState.Active,
                        Content = new Dictionary<string, object?>
                        {
                            ["$type"] = "PostTelemetry",
                            ["postPath"] = postNode.Path,
                            ["postUrn"] = urn,
                            ["sampledAt"] = importDate,
                            ["impressions"] = ParseInt(GetCell(row, cols.ImpressionsIdx)),
                            ["likes"] = ParseInt(GetCell(row, cols.LikesIdx)),
                            ["comments"] = ParseInt(GetCell(row, cols.CommentsIdx)),
                            ["shares"] = ParseInt(GetCell(row, cols.SharesIdx))
                        }
                    };
                    try { await mesh.CreateNodeAsync(node); imported++; }
                    catch { skipped++; }

                    reportProgress:
                    // Report progress every row for the first 10, then every 5th — keeps the
                    // UI responsive without flooding the data stream on huge CSVs.
                    if (i <= 10 || i % 5 == 0)
                    {
                        progress?.Invoke(
                            "### Importing... " + i + " / " + totalDataRows + "\n\n" +
                            "- Imported: " + imported + "\n" +
                            "- Unmatched: " + unmatched + "\n" +
                            "- Skipped: " + skipped + "\n");
                    }
                }

                return "### Import done\n\n" +
                       "- Imported: " + imported + "\n" +
                       "- Skipped: " + skipped + "\n" +
                       "- Unmatched: " + unmatched + "\n";
            }

            public sealed record ColumnMap(int UrnIdx, int UrlIdx, int ImpressionsIdx, int LikesIdx, int CommentsIdx, int SharesIdx);

            public static ColumnMap MapColumns(List<string> header)
            {
                int Find(params string[] keywords)
                {
                    for (int i = 0; i < header.Count; i++)
                    {
                        var h = header[i].ToLowerInvariant();
                        if (keywords.Any(k => h.Contains(k))) return i;
                    }
                    return -1;
                }
                return new ColumnMap(
                    UrnIdx: Find("urn"),
                    UrlIdx: Find("url", "link", "permalink"),
                    ImpressionsIdx: Find("impression", "reach", "views"),
                    LikesIdx: Find("reaction", "like"),
                    CommentsIdx: Find("comment"),
                    SharesIdx: Find("repost", "share"));
            }

            public static string? ExtractUrn(List<string> row, ColumnMap cols)
            {
                if (cols.UrnIdx >= 0)
                {
                    var v = GetCell(row, cols.UrnIdx);
                    if (!string.IsNullOrWhiteSpace(v) && v.StartsWith("urn:li:")) return v.Trim();
                }
                if (cols.UrlIdx >= 0)
                {
                    var url = GetCell(row, cols.UrlIdx);
                    if (string.IsNullOrWhiteSpace(url)) return null;
                    var m = Regex.Match(url, "urn%3Ali%3A(activity|share|ugcPost)%3A(\\d+)");
                    if (m.Success) return "urn:li:" + m.Groups[1].Value + ":" + m.Groups[2].Value;
                    m = Regex.Match(url, "urn:li:(activity|share|ugcPost):(\\d+)");
                    if (m.Success) return m.Value;
                    m = Regex.Match(url, "linkedin\\.com/posts/[^/]+-(\\d{15,})-");
                    if (m.Success) return "urn:li:activity:" + m.Groups[1].Value;
                }
                return null;
            }

            private static string GetCell(List<string> row, int idx) => idx < 0 || idx >= row.Count ? "" : row[idx];

            private static int ParseInt(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return 0;
                var clean = new string(raw.Where(c => char.IsDigit(c) || c == '-').ToArray());
                return int.TryParse(clean, out var v) ? v : 0;
            }

            public static List<List<string>> ParseCsv(string text)
            {
                var rows = new List<List<string>>();
                var current = new List<string>();
                var field = new System.Text.StringBuilder();
                bool inQuotes = false;
                int i = 0;
                while (i < text.Length)
                {
                    var c = text[i];
                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                            inQuotes = false; i++; continue;
                        }
                        field.Append(c); i++; continue;
                    }
                    if (c == '"') { inQuotes = true; i++; continue; }
                    if (c == ',') { current.Add(field.ToString()); field.Clear(); i++; continue; }
                    if (c == '\r') { i++; continue; }
                    if (c == '\n')
                    {
                        current.Add(field.ToString()); field.Clear();
                        rows.Add(current); current = new List<string>(); i++; continue;
                    }
                    field.Append(c); i++;
                }
                if (field.Length > 0 || current.Count > 0)
                {
                    current.Add(field.ToString());
                    rows.Add(current);
                }
                return rows;
            }

            private static async Task<bool> Exists(IMeshService mesh, string path)
            {
                await foreach (var _ in mesh.QueryAsync<MeshNode>("path:" + path)) return true;
                return false;
            }

            private static string? TryGetString(MeshNode node, string field)
            {
                if (node.Content is JsonElement je)
                {
                    if (je.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
                    var pascal = char.ToUpperInvariant(field[0]) + field[1..];
                    if (je.TryGetProperty(pascal, out var p2) && p2.ValueKind == JsonValueKind.String) return p2.GetString();
                }
                return null;
            }
        }
        """;
}
