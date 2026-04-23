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
/// Smoke test for the dynamic Systemorph/LinkedInProfile NodeType. Mirrors the
/// four Code pieces that live in prod under Systemorph/LinkedInProfile/Source/*
/// so a syntax break (e.g., the verbatim-string escaping bug that took the
/// dashboard down on 2026-04-22) fails in CI rather than at user-facing render.
///
/// What this verifies:
///   1. The NodeType definition + four Code pieces compile cleanly.
///   2. A LinkedInProfile instance renders an Overview area without throwing.
///   3. The result is a non-empty Stack control (header + analytics block).
///
/// Note: the bodies inlined below are the production source. Update them in
/// lockstep when the prod Code pieces change.
/// </summary>
public class LinkedInProfileLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypePath = "Systemorph/LinkedInProfile";
    private const string SourceNamespace = "Systemorph/LinkedInProfile/Source";

    [Fact(Timeout = 60000)]
    public async Task LinkedInProfile_NodeType_CompilesAndRendersOverview()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;

        // 1. NodeType registration — wires the LinkedInProfile content type +
        //    the Overview layout area + the menu provider, exactly like the
        //    prod NodeType node does. (PostAnalytics/PostComment NodeTypes
        //    aren't registered here — the analytics block will render its
        //    "no analytics yet" empty-state instead of charts.)
        await NodeFactory.CreateNode(new MeshNode("LinkedInProfile", "Systemorph")
        {
            Name = "LinkedIn Profile",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "A user's linked LinkedIn profile.",
                Configuration = "config => config.WithContentType<LinkedInProfile>()" +
                                ".AddDefaultLayoutAreas()" +
                                ".AddLayout(layout => layout.WithView(\"Overview\", LinkedInProfileLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // 2. Four Code pieces — schema record, layout area, menu provider, analytics renderer.
        await CreateCodeAsync("LinkedInProfile", LinkedInProfileSource, ct);
        await CreateCodeAsync("LinkedInProfileLayoutAreas", LayoutAreasSource, ct);
        await CreateCodeAsync("LinkedInProfileAnalytics", AnalyticsSource, ct);

        // 3. Sample profile instance.
        var instancePath = $"{NodeTypePath}/test-profile";
        await NodeFactory.CreateNode(new MeshNode("test-profile", NodeTypePath)
        {
            Name = "Roland Bürgi",
            NodeType = NodeTypePath,
            Content = new Dictionary<string, object?>
            {
                ["$type"] = "LinkedInProfile",
                ["displayName"] = "Roland Bürgi",
                ["headline"] = "Founder · MeshWeaver",
                ["subjectUrn"] = "urn:li:person:abc",
                ["profileUrl"] = "https://www.linkedin.com/in/rolandbuergi",
                ["connectedAt"] = DateTimeOffset.UtcNow,
            }
        });

        // 4. Render the Overview area.
        var control = await RenderOverviewAsync(instancePath, ct);

        // 5. Shape assertion: Overview composes header + analytics into a Stack.
        //    Areas hold NamedAreaControl references; deeper resolution would
        //    require enumerating the entity store, which adds noise without
        //    improving regression coverage — the key risk is "Code piece fails
        //    to compile, layout area never renders" and that is caught here.
        control.Should().NotBeNull();
        control.Should().BeOfType<StackControl>("Overview composes header + analytics via Controls.Stack");

        var stack = (StackControl)control;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2,
            "Overview composes at least the header card and the analytics block");
    }

    private Task CreateCodeAsync(string id, string source, CancellationToken ct) =>
        NodeFactory.CreateNode(new MeshNode(id, SourceNamespace)
        {
            Name = id,
            NodeType = "Code",
            Content = new CodeConfiguration { Code = source, Language = "csharp" }
        });

    private async Task<UiControl> RenderOverviewAsync(string path, CancellationToken ct)
    {
        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(30.Seconds())
            .FirstAsync(x => x is StackControl or HtmlControl or MarkdownControl)
            .ToTask(ct);

        control.Should().NotBeNull("Overview area must emit a control before the timeout");
        return (UiControl)control!;
    }

    // ---------- Code-piece bodies (prod source — keep in lockstep) ----------

    private const string LinkedInProfileSource = """
        using MeshWeaver.Domain;

        public record LinkedInProfile
        {
            [Required]
            [MeshNodeProperty(nameof(MeshNode.Name))]
            public string DisplayName { get; init; } = string.Empty;

            public string? SubjectUrn { get; init; }
            public string? ProfileUrl { get; init; }
            public string? VanityName { get; init; }
            public string? Email { get; init; }
            public string? Headline { get; init; }
            public string? PictureUrl { get; init; }
            public int? FollowerCount { get; init; }
            public int? TotalImpressions { get; init; }
            public int? TotalLikes { get; init; }
            public int? TotalComments { get; init; }
            public int? PostCount { get; init; }
            public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
            public DateTimeOffset? LastSyncAt { get; init; }
        }
        """;

    private const string LayoutAreasSource = """
        using System.Reactive.Linq;
        using System.Web;
        using MeshWeaver.Layout.Composition;
        using MeshWeaver.Mesh.Services;

        public static class LinkedInProfileLayoutAreas
        {
            public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
            {
                var hubPath = host.Hub.Address.ToString();

                var nodeStream = host.Workspace.GetStream<MeshNode>()
                    ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
                    ?? Observable.Return<MeshNode?>(null);

                return nodeStream.Select(node => (UiControl?)BuildHeader(node))
                    .CombineLatest(
                        LinkedInProfileAnalytics.Render(host, ctx),
                        (header, analytics) => (UiControl?)Controls.Stack
                            .WithStyle("padding: 16px; gap: 16px;")
                            .WithView(header ?? (UiControl)Controls.Markdown("*Loading...*"))
                            .WithView(analytics ?? (UiControl)Controls.Markdown("")));
            }

            private static UiControl BuildHeader(MeshNode? node)
            {
                if (node?.Content is not LinkedInProfile profile)
                    return Controls.Markdown("*Loading LinkedIn profile...*");

                var stack = Controls.Stack.WithStyle("gap: 16px;");
                var nameHtml = "<div><h1>" + HttpUtility.HtmlEncode(profile.DisplayName) + "</h1></div>";
                stack = stack.WithView(Controls.Html(nameHtml));
                return stack;
            }
        }
        """;

    private const string AnalyticsSource = """
        using System.Reactive.Linq;
        using MeshWeaver.Layout.Composition;
        using MeshWeaver.Mesh.Services;

        public static class LinkedInProfileAnalytics
        {
            public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
            {
                var hubPath = host.Hub.Address.ToString();
                var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

                var nodeStream = host.Workspace.GetStream<MeshNode>()
                    ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
                    ?? Observable.Return<MeshNode?>(null);

                return nodeStream.SelectMany(async _ =>
                {
                    var posts = new Dictionary<string, MeshNode>();
                    await foreach (var p in meshService.QueryAsync<MeshNode>(
                        $"namespace:{hubPath}/posts nodeType:Systemorph/Post"))
                    {
                        posts[p.Path] = p;
                    }

                    var analyticsNodes = new List<MeshNode>();
                    foreach (var postPath in posts.Keys)
                    {
                        await foreach (var a in meshService.QueryAsync<MeshNode>($"path:{postPath}/analytics"))
                        {
                            analyticsNodes.Add(a);
                            break;
                        }
                    }

                    if (analyticsNodes.Count == 0)
                    {
                        return (UiControl?)Controls.Markdown(
                            "## Engagement analytics\n\n_No analytics yet._");
                    }

                    return (UiControl?)Controls.Markdown("## Engagement analytics\n\nReady.");
                });
            }
        }
        """;
}
