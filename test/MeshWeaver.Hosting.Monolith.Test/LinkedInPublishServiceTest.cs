using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Social;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration test for <see cref="LinkedInPublishService"/> against a REAL monolith mesh — the
/// credential-read → publish → node-write-back chain the <c>POST /linkedin/publish</c> endpoint runs.
/// The only stub is the outbound LinkedIn <see cref="HttpClient"/> (a test message handler); the mesh
/// (persistence, per-node hubs, AccessContext) is real and never mocked.
/// </summary>
public class LinkedInPublishServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)                       // default: RLS + PublicAdminAccess (admin everywhere)
            .AddApiCredentialType()                          // registers the ApiCredential NodeType + PlatformCredential
            .AddMeshNodes(SocialTestNodeTypes.PostNodeType); // registers the (generic) Systemorph/Post NodeType

    [Fact(Timeout = 60000)]
    public async Task PublishPost_writes_Published_status_and_urn_back_to_the_node()
    {
        var profile = $"TestData/pub_{Guid.NewGuid():N}";
        var postPath = $"{profile}/posts/post1";

        await SeedCredentialAsync(profile, scope: "openid profile email w_member_social");
        await SeedPostAsync(postPath, profile, body: "Hello from the mesh 👋");

        // Stub LinkedIn: 201 Created + the created post URN in the x-restli-id header.
        var handler = new StubHandler(HttpStatusCode.Created, ("x-restli-id", "urn:li:share:9001"));
        var svc = new LinkedInPublishService(Mesh, NodeFactory);

        var outcome = await svc.PublishPostAsync(
            new HttpClient(handler), postPath, textOverride: null, visibility: null,
            LinkedInPostsApi.DefaultApiVersion, TestContext.Current.CancellationToken);

        outcome.Success.Should().BeTrue();
        outcome.Urn.Should().Be("urn:li:share:9001");
        outcome.HttpAttempted.Should().BeTrue();
        handler.CallCount.Should().Be(1);

        // Read the node back from the mesh (live stream) and assert the write-back landed.
        var published = await Mesh.GetWorkspace().GetMeshNodeStream(postPath)
            .Should().Within(30.Seconds())
            .Match(n => StrProp(n, "status") == "Published");
        StrProp(published, "publishedUrn").Should().Be("urn:li:share:9001");
    }

    [Fact(Timeout = 60000)]
    public async Task PublishPost_without_w_member_social_scope_makes_no_http_call()
    {
        var profile = $"TestData/pub_{Guid.NewGuid():N}";
        var postPath = $"{profile}/posts/post1";

        // Credential predates the widened scope — it lacks w_member_social.
        await SeedCredentialAsync(profile, scope: "openid profile email");
        await SeedPostAsync(postPath, profile, body: "Should never leave the mesh");

        var handler = new StubHandler(HttpStatusCode.Created, ("x-restli-id", "urn:li:share:should-not-happen"));
        var svc = new LinkedInPublishService(Mesh, NodeFactory);

        var outcome = await svc.PublishPostAsync(
            new HttpClient(handler), postPath, textOverride: null, visibility: null,
            LinkedInPostsApi.DefaultApiVersion, TestContext.Current.CancellationToken);

        // The missing-scope guard fires BEFORE any LinkedIn call, and nothing is written back.
        outcome.Success.Should().BeFalse();
        outcome.Reason.Should().Be("missing-w_member_social-reconnect");
        outcome.HttpAttempted.Should().BeFalse();
        handler.CallCount.Should().Be(0);

        var node = await Mesh.GetWorkspace().GetMeshNodeStream(postPath)
            .Should().Within(15.Seconds())
            .Match(n => StrProp(n, "body") is not null);
        StrProp(node, "status").Should().Be("Draft");
    }

    private Task SeedCredentialAsync(string profile, string scope) =>
        NodeFactory.CreateNode(new MeshNode("linkedin", $"{profile}/_ApiCredentials")
        {
            Name = "LinkedIn credential",
            NodeType = ApiCredentialNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new PlatformCredential
            {
                Platform = LinkedInPublisher.PlatformId,
                SubjectId = "abc123",
                AccessToken = "test-token",
                Scope = scope,
                AcquiredAt = DateTimeOffset.UtcNow,
            }
        }).Should().Within(30.Seconds()).Emit();

    private Task SeedPostAsync(string postPath, string profile, string body)
    {
        var (id, ns) = SplitPath(postPath);
        return NodeFactory.CreateNode(new MeshNode(id, ns)
        {
            Name = "Test post",
            NodeType = "Systemorph/Post",
            State = MeshNodeState.Active,
            Content = new Dictionary<string, object?>
            {
                ["title"] = "Test post",
                ["body"] = body,
                ["profilePath"] = profile,
                ["platform"] = "LinkedIn",
                ["status"] = "Draft",
            }
        }).Should().Within(30.Seconds()).Emit();
    }

    internal static (string Id, string Namespace) SplitPath(string path)
    {
        var i = path.LastIndexOf('/');
        return (path[(i + 1)..], path[..i]);
    }

    internal static string? StrProp(MeshNode? node, string name)
    {
        if (node?.Content is null) return null;
        var je = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, node.Content.GetType());
        if (je.ValueKind != JsonValueKind.Object) return null;
        if (je.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return je.TryGetProperty(pascal, out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
    }

    /// <summary>Test LinkedIn HTTP boundary: returns a canned response and counts invocations.</summary>
    internal sealed class StubHandler(HttpStatusCode status, params (string Name, string Value)[] responseHeaders)
        : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in responseHeaders)
                resp.Headers.TryAddWithoutValidation(name, value);
            return Task.FromResult(resp);
        }
    }
}
