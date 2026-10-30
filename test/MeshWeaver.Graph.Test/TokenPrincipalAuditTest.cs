using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The read-only token principal audit (#7598): every token record whose principal is not bound to
/// it is LISTED, and one that is bound is not. A request created by a platform admin runs as System
/// and writes its result back onto itself. One created by anyone else is refused by name and sweeps
/// nothing. Nothing is ever revoked, and no hash is ever copied into the report.
/// </summary>
public class TokenPrincipalAuditTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PlatformAdmin = "audit-admin";
    private const string Alice = "audit-alice";
    private const string Bob = "audit-bob";
    private const string Service = "svc-audit";
    private static readonly string Hash = new('f', 64);

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // ConfigureMeshBase: no blanket Public-admin grant, so only the platform admin is one.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminAppNodeType.Path),
            AssignmentNodeFactory.UserRole(Alice, "Admin", Alice));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static MeshNode Token(string id, string ns, string principal, string? serviceIdentityPath = null) =>
        new(id, ns)
        {
            Name = id, NodeType = "ApiToken",
            Content = new ApiToken { TokenHash = Hash, UserId = principal, Label = id, ServiceIdentityPath = serviceIdentityPath },
        };

    private async Task Seed(CancellationToken ct)
    {
        foreach (var node in new[]
                 {
                     Token("bound", $"{Alice}/ApiToken", Alice),
                     Token("renamed", $"{Alice}/ApiToken", Bob),
                     Token("system", $"{Alice}/ApiToken", WellKnownUsers.System),
                     Token("svc", $"{ServiceIdentity.TokenNamespace(Service)}", Service, ServiceIdentity.PathFor(Service)),
                     new MeshNode("aaaaaaaaaaaa", ApiTokenVerdict.IndexNamespace)
                     {
                         Name = "index", NodeType = "ApiToken",
                         Content = new ApiTokenIndex { TokenHash = Hash, TokenPath = $"{Alice}/ApiToken/bound" },
                     },
                 })
            await Access.RunAsSystem(() => NodeFactory.CreateNode(node))
                .Should().Within(Budget).Emit($"seed {node.Path}", ct);
    }

    /// <summary>A mismatching token is listed, a matching one is not, and no hash leaves the record.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSweep_ListsTheUnboundTokens_AndNotTheBoundOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var result = await TokenPrincipalAuditing.Sweep(Mesh).Should().Within(Budget).Emit("the sweep must answer", ct);
        Output.WriteLine($"tokens read={result.TokensRead} partitions={(result.PartitionsRead is null ? "unknown" : string.Join(",", result.PartitionsRead))} complete={result.Complete} ({result.Incomplete})");
        foreach (var m in result.Mismatches)
            Output.WriteLine($"  {m.Path} principal={m.Principal} findings={string.Join(",", m.Findings)}");

        var listed = result.Mismatches.Select(m => m.Path).ToArray();
        listed.Should().Contain($"{Alice}/ApiToken/renamed", "a token naming another person in Alice's partition is listed");
        listed.Should().Contain($"{Alice}/ApiToken/system", "and one naming System");
        listed.Should().NotContain($"{Alice}/ApiToken/bound", "a token naming its own partition's person is not");
        listed.Should().NotContain($"{ServiceIdentity.TokenNamespace(Service)}/svc", "nor a service token in its own namespace");
        result.TokensRead.Should().BeGreaterThan(3, "the denominator counts every token record read — the bound ones too");
        result.Mismatches.Single(m => m.Path.EndsWith("/system", StringComparison.Ordinal)).Findings
            .Should().Contain(TokenPrincipalFinding.NamesSystem);
        System.Text.Json.JsonSerializer.Serialize(result, Mesh.JsonSerializerOptions).Should().NotContain(Hash,
            "the report never carries a token hash");
    }

    /// <summary>
    /// End to end: a platform admin's request runs as System and writes the result onto itself;
    /// an ordinary user's request is refused by name and sweeps nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequest_RunsForAPlatformAdmin_AndIsRefusedForAnyoneElse()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var byAdmin = await Request("by-admin", PlatformAdmin, ct);
        byAdmin.Status.Should().Be(TokenPrincipalAuditStatus.Done, byAdmin.Error ?? "");
        byAdmin.RequestedBy.Should().Be(PlatformAdmin);
        byAdmin.Mismatches.Select(m => m.Path).Should().Contain($"{Alice}/ApiToken/renamed");

        // An ordinary user cannot even create the request (the Admin partition is a platform admin's);
        // were one to get there, the executor would refuse it by name before sweeping.
        Access.SetContext(new AccessContext { ObjectId = Alice, Name = Alice });
        var byUser = await NodeFactory.CreateNode(new MeshNode("by-user", TokenPrincipalAudit.Namespace)
            {
                Name = "by-user", NodeType = TokenPrincipalAudit.NodeType,
                Content = new TokenPrincipalAudit { Reason = "test" },
            })
            .Take(1).Select(_ => "created")
            // ONLY a permission refusal counts (review of #7619): any other fault propagates and fails
            // the test instead of passing as a "refusal".
            .Catch((UnauthorizedAccessException ex) => Observable.Return("refused:" + ex.Message))
            .Should().Within(Budget).Emit("the create must answer", ct);
        Output.WriteLine($"request by an ordinary user: {byUser}");
        if (byUser == "created")
            (await Settled("by-user", ct)).Status.Should().Be(TokenPrincipalAuditStatus.Refused,
                "only a platform admin may run the audit");
        else
            byUser.Should().Contain("Create permission").And.Contain($"{TokenPrincipalAudit.Namespace}/by-user",
                "the refusal is the Admin partition's create permission, for this request");
    }

    /// <summary>
    /// 🚨 A node of this type OUTSIDE the protected namespace never runs (review of #7619): an editor
    /// who retypes an admin-authored node elsewhere keeps its admin creator, and the result would be
    /// written where that editor can read it. The node stays exactly as written — no status, no sweep.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestOutsideTheProtectedNamespace_IsNeverRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        const string elsewhere = "AuditElsewhere/req";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("req", "AuditElsewhere")
            {
                Name = "req", NodeType = TokenPrincipalAudit.NodeType,
                Content = new TokenPrincipalAudit { Reason = "test" },
            }))
            .Should().Within(Budget).Emit("the node must exist", ct);

        // Activate it (a read), then give its initialization ample turns; a request that would run
        // reaches Running within one.
        var read = await Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(elsewhere))
            .Where(n => n is not null).Select(n => n!)
            .Should().Within(Budget).Emit("the node must be readable", ct);
        read.ContentAs<TokenPrincipalAudit>(Mesh.JsonSerializerOptions)!.Status.Should().BeNull();
        await Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(elsewhere))
            .Select(n => n?.ContentAs<TokenPrincipalAudit>(Mesh.JsonSerializerOptions)?.Status)
            .Where(status => status is not null)
            .Should().NotEmit(TimeSpan.FromSeconds(3), "a request outside Admin/_TokenAudit is never run", ct);
        TokenPrincipalAuditing.IsInProtectedNamespace(elsewhere).Should().BeFalse();
        TokenPrincipalAuditing.IsInProtectedNamespace($"{TokenPrincipalAudit.Namespace}/x").Should().BeTrue();
    }

    /// <summary>The request, created by <paramref name="creator"/> as themselves, then read until it settles.</summary>
    private async Task<TokenPrincipalAudit> Request(string id, string creator, CancellationToken ct)
    {
        Access.SetContext(new AccessContext { ObjectId = creator, Name = creator });
        await NodeFactory.CreateNode(new MeshNode(id, TokenPrincipalAudit.Namespace)
            {
                Name = id, NodeType = TokenPrincipalAudit.NodeType,
                Content = new TokenPrincipalAudit { Reason = "test" },
            })
            .Should().Within(Budget).Emit("the request must exist", ct);
        return await Settled(id, ct);
    }

    private Task<TokenPrincipalAudit> Settled(string id, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream($"{TokenPrincipalAudit.Namespace}/{id}"))
            .Select(n => n?.ContentAs<TokenPrincipalAudit>(Mesh.JsonSerializerOptions))
            .Where(c => c?.Status is TokenPrincipalAuditStatus.Done or TokenPrincipalAuditStatus.Refused or TokenPrincipalAuditStatus.Failed)
            .Select(c => c!)
            .Should().Within(Budget).Emit("the request must settle", ct);
}
