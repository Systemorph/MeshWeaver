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
/// Compile-guard test for the <c>LinkedInPullActions</c> Code piece that lives
/// under <c>Systemorph/LinkedInProfile/Source/</c> in the production mesh. The
/// body inlined here is the authoritative production source — keep it in
/// lockstep so any drift (missing <c>using</c>, syntax slip, wrong interface
/// shape) fails CI instead of the user-facing LinkedIn dashboard.
///
/// Regression for 2026-04-22 incident: the Code piece shipped without
/// <c>using System.Threading.Tasks;</c>, the <c>Task</c>/<c>Task&lt;&gt;</c>
/// helper method signatures failed to resolve, and the whole NodeType stopped
/// compiling — the dashboard rendered the raw compiler diagnostic.
/// </summary>
public class LinkedInPullActionsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypePath = "Systemorph/LinkedInProfile";
    private const string SourceNamespace = "Systemorph/LinkedInProfile/Source";

    [Fact(Timeout = 60000)]
    public async Task LinkedInPullActions_CompilesAndRendersNoCredentialBranch()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;

        // Register the NodeType with the PullPastPosts layout area wired in —
        // this is the exact Configuration string that lives in prod.
        await NodeFactory.CreateNode(new MeshNode("LinkedInProfile", "Systemorph")
        {
            Name = "LinkedIn Profile",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "A user's linked LinkedIn profile.",
                Configuration =
                    "config => config.WithContentType<LinkedInProfile>()" +
                    ".AddDefaultLayoutAreas()" +
                    ".AddLayout(layout => layout.WithView(\"PullPastPosts\", LinkedInPullActions.PullPastPosts))",
                ShowChildrenInDetails = false,
            }
        });

        await CreateCodeAsync("LinkedInProfile", LinkedInProfileSource, ct);
        await CreateCodeAsync("LinkedInPullActions", LinkedInPullActionsSource, ct);

        var instancePath = $"{NodeTypePath}/test-profile";
        await NodeFactory.CreateNode(new MeshNode("test-profile", NodeTypePath)
        {
            Name = "Roland",
            NodeType = NodeTypePath,
            Content = new Dictionary<string, object?>
            {
                ["$type"] = "LinkedInProfile",
                ["displayName"] = "Roland",
                ["connectedAt"] = DateTimeOffset.UtcNow,
            }
        });

        // Render the PullPastPosts area. In the test mesh LinkedInPublisher is
        // NOT registered in DI, so the area hits its "publisher is null" branch
        // and returns a MarkdownControl describing the missing dependency.
        // That branch still exercises the full compile of the Code piece —
        // which is what we're guarding.
        var control = await RenderAreaAsync(instancePath, "PullPastPosts", ct);

        control.Should().NotBeNull();
        control.Should().BeOfType<MarkdownControl>(
            "PullPastPosts returns Markdown whether the publisher is present (progress report) " +
            "or absent (missing-DI warning)");
    }

    private Task CreateCodeAsync(string id, string source, CancellationToken ct) =>
        NodeFactory.CreateNode(new MeshNode(id, SourceNamespace)
        {
            Name = id,
            NodeType = "Code",
            Content = new CodeConfiguration { Code = source, Language = "csharp" }
        });

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
            .FirstAsync(x => x is MarkdownControl or StackControl or HtmlControl)
            .ToTask(ct);

        control.Should().NotBeNull("area must emit a control before the timeout");
        return (UiControl)control!;
    }

    // ---------- Production source (keep in lockstep) ----------

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
    /// Mirror of <c>Systemorph/LinkedInProfile/Source/LinkedInPullActions</c>
    /// as shipped in prod. If you edit the Code piece via MCP, update this
    /// literal too so the compile-guard stays honest.
    /// </summary>
    private const string LinkedInPullActionsSource = """
        using System.Reactive.Linq;
        using System.Text.Json;
        using System.Threading.Tasks;
        using System.Web;
        using MeshWeaver.Layout.Composition;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;
        using MeshWeaver.Social;

        public static class LinkedInPullActions
        {
            public static IObservable<UiControl?> PullPastPosts(LayoutAreaHost host, RenderingContext _)
            {
                var hubPath = host.Hub.Address.ToString();
                var mesh = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                var publisher = host.Hub.ServiceProvider.GetService<LinkedInPublisher>();

                return Observable.FromAsync(async () =>
                {
                    if (publisher is null)
                        return (UiControl?)Controls.Markdown("## Pull past posts\n\n_LinkedInPublisher is not registered in DI._");

                    var credential = await LoadCredential(mesh, hubPath);
                    if (credential is null)
                        return (UiControl?)Controls.Markdown($"## Pull past posts\n\n_No credential at `{hubPath}/_ApiCredentials/linkedin`._");

                    int imported = 0, skipped = 0;
                    await foreach (var past in publisher.ListPastPostsAsync(credential, sinceInclusive: null, maxItems: 200, default))
                    {
                        var id = SanitizeUrn(past.Urn);
                        var path = $"{hubPath}/posts/{id}";
                        if (await Exists(mesh, path)) { skipped++; continue; }

                        var node = new MeshNode(id, $"{hubPath}/posts")
                        {
                            Name = TruncateForName(past.Text),
                            NodeType = "Systemorph/Post",
                            State = MeshNodeState.Active,
                            Content = new Dictionary<string, object?>
                            {
                                ["$type"] = "SocialMediaPost",
                                ["title"] = TruncateForName(past.Text),
                                ["body"] = past.Text,
                                ["profilePath"] = hubPath,
                                ["platform"] = "LinkedIn",
                                ["publishedAt"] = past.PublishedAt,
                                ["platformUrn"] = past.Urn,
                                ["platformUrl"] = past.PostUrl,
                                ["impressions"] = past.Stats?.Impressions ?? 0,
                                ["likes"] = past.Stats?.Likes ?? 0,
                            }
                        };
                        try { await mesh.CreateNode(node); imported++; } catch { }
                    }

                    return (UiControl?)Controls.Markdown($"## Pull past posts done\n\nImported: {imported}, skipped: {skipped}.");
                });
            }

            private static async Task<PlatformCredential?> LoadCredential(IMeshService mesh, string profilePath)
            {
                await foreach (var n in mesh.QueryAsync<MeshNode>($"path:{profilePath}/_ApiCredentials/linkedin"))
                {
                    if (n.Content is PlatformCredential typed) return typed;
                    if (n.Content is JsonElement je)
                        return je.Deserialize<PlatformCredential>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return null;
                }
                return null;
            }

            private static async Task<bool> Exists(IMeshService mesh, string path)
            {
                await foreach (var _ in mesh.QueryAsync<MeshNode>($"path:{path}")) return true;
                return false;
            }

            private static string SanitizeUrn(string urn) =>
                urn.Replace(':', '_').Replace('/', '_').Replace('?', '_');

            private static string TruncateForName(string text)
            {
                var t = (text ?? "").ReplaceLineEndings(" ").Trim();
                if (t.Length == 0) return "(untitled)";
                return t.Length > 80 ? t[..80] + "…" : t;
            }
        }
        """;
}
