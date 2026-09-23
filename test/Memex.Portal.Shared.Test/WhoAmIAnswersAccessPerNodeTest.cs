#pragma warning disable CS1591

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver#5189: <c>whoami</c> answered who the caller is and never what they may do, so "am I
/// an administrator here?" could only be answered by opening an admin surface and reading its
/// refusal — which is indistinguishable from a stale session. Access is per node, so the answer is
/// asked AT an address (<see cref="MeshOperations.WhoAmI"/>) and computed by the evaluator the
/// gates themselves consult.
///
/// <para>Each case below discriminates: the same call answers differently for a Viewer, an Editor,
/// a platform admin and an anonymous caller, at a node inside and outside their grant — so a
/// WhoAmI that returned a constant, the caller's roles anywhere, or "admin ⇒ everything" fails
/// at least one of them.</para>
/// </summary>
public class WhoAmIAnswersAccessPerNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "WhoAmIProbe";
    private const string DocPath = Partition + "/Doc";
    private const string Elsewhere = "WhoAmIElsewhere";

    private const string ViewerUser = "whoami-viewer";
    private const string EditorUser = "whoami-editor";
    private const string PlatformAdminUser = "whoami-platform-admin";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every caller would hold everything and each case would pass
    // vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = "WhoAmI Probe", NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode(Elsewhere) { Name = "Elsewhere", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(ViewerUser, "Viewer", Partition),
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Partition),
                // The canonical platform-admin shape: Admin on the Admin partition, nothing else.
                AssignmentNodeFactory.UserRole(PlatformAdminUser, "Admin", "Admin"));

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh,
        "mcp",
        "whoami",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(WhoAmIAnswersAccessPerNodeTest)));

    // A caller with no identity is stamped as Anonymous — what UserContextMiddleware leaves on an
    // unauthenticated request; clearing the context would fall back to the harness's circuit user.
    private void ActAs(string? userId) => Mesh.ServiceProvider.GetRequiredService<AccessService>()
        .SetContext(new AccessContext { ObjectId = userId ?? WellKnownUsers.Anonymous, Name = userId ?? "" });

    private async Task<JsonElement> WhoAmI(string? userId, string path)
    {
        ActAs(userId);
        var json = await new MeshOperations(SessionHub()).WhoAmI(path)
            .Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.False(json.StartsWith("Error", StringComparison.Ordinal), json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string[] Permissions(JsonElement answer) =>
        [.. answer.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];

    [Fact]
    public async Task AViewer_ReadsButCannotUpdate_InsideTheirGrant()
    {
        var answer = await WhoAmI(ViewerUser, DocPath);

        Assert.Equal(ViewerUser, answer.GetProperty("userId").GetString());
        Assert.Equal(DocPath, answer.GetProperty("path").GetString());
        Assert.Contains("Read", Permissions(answer));
        Assert.DoesNotContain("Update", Permissions(answer));
        Assert.False(answer.GetProperty("isGlobalAdmin").GetBoolean());
    }

    [Fact]
    public async Task AnEditor_AtTheSameNode_IsToldTheyMayUpdate()
    {
        var answer = await WhoAmI(EditorUser, DocPath);

        Assert.Contains("Update", Permissions(answer));
        Assert.False(answer.GetProperty("isGlobalAdmin").GetBoolean());
    }

    /// <summary>The same Viewer, one partition over: the answer is per node, not per person.</summary>
    [Fact]
    public async Task TheViewer_OutsideTheirGrant_IsToldTheyMayNotRead()
        => Assert.DoesNotContain("Read", Permissions(await WhoAmI(ViewerUser, Elsewhere)));

    /// <summary>
    /// The issue's own case: a platform admin learns they ARE one — and, at a content node, that it
    /// confers no read there (a global admin is not a data superuser, AccessControl.md).
    /// </summary>
    [Fact]
    public async Task APlatformAdmin_IsToldSo_AndThatItOpensNoContent()
    {
        var atContent = await WhoAmI(PlatformAdminUser, DocPath);

        Assert.True(atContent.GetProperty("isGlobalAdmin").GetBoolean());
        Assert.DoesNotContain("Read", Permissions(atContent));

        var atAdmin = await WhoAmI(PlatformAdminUser, "Admin");
        Assert.Contains("Update", Permissions(atAdmin));
    }

    [Fact]
    public async Task ACallerWithNoIdentity_IsAnsweredAsAnonymous_WithNoIdentityEchoed()
    {
        var answer = await WhoAmI(null, DocPath);

        Assert.Equal(JsonValueKind.Null, answer.GetProperty("userId").ValueKind);
        Assert.False(answer.GetProperty("isGlobalAdmin").GetBoolean());
        Assert.DoesNotContain("Update", Permissions(answer));
    }

    /// <summary>
    /// The REST dispatch: only a body naming a node asks for the access answer. No body, `{}` and a
    /// blank path — what portal-next's SSR, the React-Native app and the e2e send today — must
    /// stay the identity-only answer, or every existing caller changes shape.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" " + DocPath + " ", true)]
    public void TheRestBody_AsksForAccessOnlyWhenItNamesANode(string? path, bool asks)
    {
        var body = path is null ? null : new MeshApiEndpoints.WhoAmIBody(path);
        Assert.Equal(asks, MeshApiEndpoints.AsksForAccess(body, out var resolved));
        if (asks)
            Assert.Equal(DocPath, resolved);
        Assert.False(MeshApiEndpoints.AsksForAccess(null, out _), "an omitted body is the identity-only answer");
    }

    /// <summary>
    /// And the real mapped route binds that body OPTIONALLY: an SSR caller posting no body at all
    /// must not be refused with 400 by the binder before the handler runs.
    /// </summary>
    [Fact]
    public async Task TheWhoAmIRoute_BindsItsBodyOptionally()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.UseTestServer(builder.WebHost);
        // Parameter binding must see IMessageHub as a SERVICE; never resolved — nothing is invoked.
        builder.Services.AddSingleton<IMessageHub>(_ => null!);
        var app = builder.Build();
        await using (app)
        {
            Memex.Portal.Shared.Api.MeshApiEndpoints.MapMeshApi(app);
            var endpoint = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == "/api/mesh/whoami");
            var accepts = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>();
            Assert.NotNull(accepts);
            Assert.Equal(typeof(MeshApiEndpoints.WhoAmIBody), accepts!.RequestType);
            Assert.True(accepts.IsOptional, "a missing body must reach the identity-only answer, not a 400");
        }
    }

    [Fact]
    public async Task NoAddress_IsRefused_BecauseAccessIsPerNode()
    {
        ActAs(ViewerUser);
        var json = await new MeshOperations(SessionHub()).WhoAmI("  ")
            .Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.StartsWith("Error: path is required", json);
    }
}
