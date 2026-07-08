using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Memex.Portal.Shared.Social;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Access-control integration tests for <see cref="LinkedInPublishService"/> against a real
/// row-level-security mesh (no <c>PublicAdminAccess</c> — only the seeded per-user grants apply).
/// Proves the two access rules the endpoint promises, enforced by the NORMAL mesh permission checks
/// under the caller's AccessContext (the service never runs as system):
///
///   (a) a user WITHOUT Update on the post cannot publish it — no LinkedIn call, no write-back;
///   (b) a user cannot publish using another profile's credential — the credential read is denied.
///
/// <para><c>bob</c> is a Viewer on <c>SpaceA</c> (read-only) and an Editor on <c>SpaceB</c>, and has
/// no access to <c>SpaceC</c>. Setup nodes are created as system (the legitimate provisioner); the
/// publish attempts run as <c>bob</c>.</para>
/// </summary>
public class LinkedInPublishAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)   // ConfigureMeshBase enables AddRowLevelSecurity(); no PublicAdminAccess here.
            .AddApiCredentialType()
            .AddMeshNodes(SocialTestNodeTypes.PostNodeType)
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("bob_viewerA", "Viewer", "SpaceA", accessObject: "bob"),
                AssignmentNodeFactory.UserRole("bob_editorB", "Editor", "SpaceB", accessObject: "bob"));

    [Fact(Timeout = 60000)]
    public async Task Publish_is_denied_for_a_user_without_update_on_the_post()
    {
        // bob is only a Viewer on SpaceA — he can READ the post but not UPDATE it.
        await CreateAsSystem(Credential("SpaceA", scope: "openid profile email w_member_social"));
        await CreateAsSystem(Post("SpaceA/posts/postA", profile: "SpaceA", body: "someone else's post"));

        Login("bob");
        var handler = new LinkedInPublishServiceTest.StubHandler(HttpStatusCode.Created, ("x-restli-id", "urn:li:share:nope"));
        var svc = new LinkedInPublishService(Mesh, NodeFactory);

        var outcome = await svc.PublishPostAsync(
            new HttpClient(handler), "SpaceA/posts/postA", textOverride: null, visibility: null,
            LinkedInPostsApi.DefaultApiVersion, TestContext.Current.CancellationToken);

        outcome.Success.Should().BeFalse();
        outcome.Reason.Should().Be("access-denied");
        outcome.HttpAttempted.Should().BeFalse();
        handler.CallCount.Should().Be(0);   // nothing was published

        // Nothing was written back either — Status stays Draft (bob can still READ it).
        var node = await Mesh.GetWorkspace().GetMeshNodeStream("SpaceA/posts/postA")
            .Should().Within(15.Seconds())
            .Match(n => LinkedInPublishServiceTest.StrProp(n, "body") is not null);
        LinkedInPublishServiceTest.StrProp(node, "status").Should().Be("Draft");
    }

    [Fact(Timeout = 60000)]
    public async Task Publish_cannot_borrow_another_profiles_credential()
    {
        // The credential lives under SpaceC (bob has NO access). bob owns postB (Editor on SpaceB) and
        // points it at SpaceC's profile — he can update the post but must not be able to publish with a
        // credential he cannot read.
        await CreateAsSystem(Credential("SpaceC", scope: "openid profile email w_member_social"));
        await CreateAsSystem(Post("SpaceB/posts/postB", profile: "SpaceC", body: "bob borrowing a credential"));

        Login("bob");
        var handler = new LinkedInPublishServiceTest.StubHandler(HttpStatusCode.Created, ("x-restli-id", "urn:li:share:nope"));
        var svc = new LinkedInPublishService(Mesh, NodeFactory);

        var outcome = await svc.PublishPostAsync(
            new HttpClient(handler), "SpaceB/posts/postB", textOverride: null, visibility: null,
            LinkedInPostsApi.DefaultApiVersion, TestContext.Current.CancellationToken);

        outcome.Success.Should().BeFalse();
        outcome.Reason.Should().Be("not-connected");   // credential read denied → resolved as no credential
        outcome.HttpAttempted.Should().BeFalse();
        handler.CallCount.Should().Be(0);

        var node = await Mesh.GetWorkspace().GetMeshNodeStream("SpaceB/posts/postB")
            .Should().Within(15.Seconds())
            .Match(n => LinkedInPublishServiceTest.StrProp(n, "body") is not null);
        LinkedInPublishServiceTest.StrProp(node, "status").Should().Be("Draft");
    }

    private void Login(string userId)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });

    private Task CreateAsSystem(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(access.ImpersonateAsSystem, _ => NodeFactory.CreateNode(node))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }

    private static MeshNode Credential(string profile, string scope) =>
        new("linkedin", $"{profile}/_ApiCredentials")
        {
            Name = "LinkedIn credential",
            NodeType = ApiCredentialNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new PlatformCredential
            {
                Platform = LinkedInPublisher.PlatformId,
                SubjectId = "abc",
                AccessToken = "tok",
                Scope = scope,
                AcquiredAt = DateTimeOffset.UtcNow,
            }
        };

    private static MeshNode Post(string path, string profile, string body)
    {
        var (id, ns) = LinkedInPublishServiceTest.SplitPath(path);
        return new MeshNode(id, ns)
        {
            Name = "post",
            NodeType = "Systemorph/Post",
            State = MeshNodeState.Active,
            Content = new Dictionary<string, object?>
            {
                ["title"] = "post",
                ["body"] = body,
                ["profilePath"] = profile,
                ["platform"] = "LinkedIn",
                ["status"] = "Draft",
            }
        };
    }
}
