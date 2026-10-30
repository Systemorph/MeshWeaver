using System.Reactive.Linq;
using System.Text.Json;
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
/// 🚨 A token's principal is bound to the caller that writes it. The record's <c>UserId</c> is what
/// every token verdict authenticates as, so a person may write only a token naming THEMSELVES and may
/// never change the principal of an existing one. Otherwise the owner of a token record could rename
/// its principal and authenticate as whoever it names.
///
/// <para>The writes are made the way a signed-in person makes them, through the routed update and
/// the create chain. The authentication side is the shared verdict (<see cref="ApiTokenVerdict"/>)
/// over the stored records. The owner's own write that keeps the principal is the control.</para>
/// </summary>
public class ApiTokenPrincipalBindingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Owner = "principal-owner";
    private const string Other = "principal-other";
    private const string RawToken = "mw_principal-binding-test";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // ConfigureMeshBase: no blanket Public-admin grant, so the owner holds only what its own
    // partition gives it.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string Hash => ValidateTokenRequest.HashToken(RawToken);
    private static string RecordPath => $"{Owner}/ApiToken/{Hash[..12]}";

    private static AccessContext Person(string id) => new() { ObjectId = id, Name = id };

    private ApiToken TokenOf(MeshNode node) => ApiTokenVerdict.ReadToken(node, Mesh.JsonSerializerOptions)!;

    private async Task MintAsTheService(CancellationToken ct)
    {
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(Hash[..12], $"{Owner}/ApiToken")
            {
                Name = "API Token: binding", NodeType = ApiTokenNodeType.NodeType, MainNode = Owner,
                Content = new ApiToken { TokenHash = Hash, UserId = Owner, UserName = Owner, Label = "binding" },
            }))
            .Should().Within(Budget).Emit("the token record must exist first", ct);
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(Hash[..12], ApiTokenVerdict.IndexNamespace)
            {
                Name = "API Token: binding", NodeType = ApiTokenNodeType.NodeType,
                Content = new ApiTokenIndex { TokenHash = Hash, TokenPath = RecordPath },
            }))
            .Should().Within(Budget).Emit("the index must exist first", ct);
    }

    private Task<string> UpdateAs(string who, Func<MeshNode, MeshNode> change, CancellationToken ct)
    {
        Access.SetContext(Person(who));
        return Mesh.GetWorkspace().GetMeshNodeStream(RecordPath)
            .Update(change)
            .Take(1)
            .Select(_ => "landed")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(Budget).Emit("the update must answer", ct);
    }

    private Task<ValidateTokenResponse> Verdict(CancellationToken ct) =>
        ApiTokenVerdict.Decide(RawToken,
                path => Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(path)
                    .Where(n => n is not null).Take(1).Select(n => (MeshNode?)n)),
                Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit("the verdict must answer", ct);

    /// <summary>
    /// 🚨 THE PIN. The owner of a token record renames its principal to System, and to another
    /// person, through the routed update. Both are refused, and the token still authenticates as its
    /// owner. The owner's own revoke, which keeps the principal, lands.
    /// </summary>
    [Theory(Timeout = 90_000)]
    [InlineData(WellKnownUsers.System)]
    [InlineData(Other)]
    public async Task TheOwnerOfAToken_CannotRenameItsPrincipal(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        await MintAsTheService(ct);
        (await Verdict(ct)).UserId.Should().Be(Owner, "precondition: the token authenticates as its owner");

        var renamed = await UpdateAs(Owner, n => n with { Content = TokenOf(n) with { UserId = target } }, ct);
        Output.WriteLine($"rename to {target}: {renamed}");
        renamed.Should().StartWith("refused:", "a token's principal never changes");
        (await Verdict(ct)).UserId.Should().Be(Owner, "and the token still authenticates as its owner, never as " + target);

        (await UpdateAs(Owner, n => n with { Content = TokenOf(n) with { Label = "renamed label" } }, ct))
            .Should().Be("landed", "the control: the owner's own write that keeps the principal lands");
    }

    /// <summary>
    /// A person writes a NEW token record only for themselves. One naming System or another person is
    /// refused at the create chain, whatever the node is typed as. The owner's own record is the control.
    /// </summary>
    [Theory(Timeout = 90_000)]
    [InlineData(WellKnownUsers.System, "ApiToken")]
    [InlineData(Other, "ApiToken")]
    [InlineData(WellKnownUsers.System, "Markdown")]
    public async Task APersonCreatesATokenOnlyForThemselves(string principal, string nodeType)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetContext(Person(Owner));
        var refused = await CreateAs(principal, nodeType, "forged", ct);
        Output.WriteLine($"create naming {principal} as {nodeType}: {refused}");
        refused.Should().StartWith("refused:", $"a person never writes a token naming '{principal}'");

        (await CreateAs(Owner, "ApiToken", "own", ct)).Should().Be("created", "the control: their own token");
    }

    /// <summary>
    /// 🚨 THE LOCATION PIN (#7598, review of #7601). Whatever write route a record at the indexed path
    /// came through — rewritten, cleared and restored, confirmed from a transient, carried in a data
    /// change, reached through a cold owner, or written with content the write rules read differently —
    /// it authenticates only as the principal its LOCATION belongs to. Decided on the verdict over the
    /// stored bytes, so it needs no write route at all: the record is supplied as stored. The owner's
    /// own record and a service token in its own namespace are the controls.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(Owner + "/ApiToken/x", WellKnownUsers.System, null, false)]
    [InlineData(Owner + "/ApiToken/x", Other, null, false)]
    [InlineData(Owner + "/ApiToken/x", "svc-forged", "Admin/_ServiceIdentity/svc-forged", false)]
    [InlineData(Owner + "/ApiToken/x", Owner, null, true)]
    [InlineData("Admin/_ServiceIdentity/svc-real/ApiToken/x", "svc-real", "Admin/_ServiceIdentity/svc-real", true)]
    // Review of #7601 (round 2): a location must be CLASSIFIED before a principal is read off it —
    // the service namespace never authenticates its partition's name, the pre-migration layout
    // never its first segment, and a principal is never a multi-segment path.
    [InlineData("Admin/_ServiceIdentity/svc-real/ApiToken/x", "Admin", null, false)]
    [InlineData("User/" + Owner + "/ApiToken/x", "User", null, false)]
    [InlineData("User/" + Owner + "/ApiToken/x", Owner, null, true)]
    [InlineData(Owner + "/ApiToken/x", Owner + "/ApiToken", null, false)]
    // Review of #7601 (round 3): a record in a partition NAMED like System never authenticates as a
    // case variant of System.
    [InlineData("System-Security/ApiToken/x", "System-Security", null, false)]
    public async Task AStoredRecord_AuthenticatesOnlyAsThePrincipalOfItsLocation(
        string recordPath, string principal, string? serviceIdentityPath, bool authenticatesAsNamed)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Mesh.JsonSerializerOptions;
        var index = new MeshNode(ApiTokenVerdict.IndexPath(Hash))
        {
            NodeType = ApiTokenNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(new ApiTokenIndex { TokenHash = Hash, TokenPath = recordPath }, options),
        };
        var record = MeshNode.FromPath(recordPath) with
        {
            NodeType = ApiTokenNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(
                new ApiToken { TokenHash = Hash, UserId = principal, ServiceIdentityPath = serviceIdentityPath }, options),
        };
        var identity = serviceIdentityPath is null ? null : MeshNode.FromPath(serviceIdentityPath) with
        {
            Content = JsonSerializer.SerializeToElement(new ServiceIdentity { Description = "test" }, options),
        };
        var response = await ApiTokenVerdict
            .Decide(RawToken, path => Observable.Return<MeshNode?>(
                path == index.Path ? index : path == recordPath ? record : path == serviceIdentityPath ? identity : null), options)
            .Should().Within(Budget).Emit("the verdict must answer", ct);
        Output.WriteLine($"{recordPath} naming {principal}: success={response.Success} user={response.UserId} error={response.Error}");

        if (authenticatesAsNamed)
            response.UserId.Should().Be(principal, "the control: a record in its principal's own location authenticates");
        else
            response.Success.Should().BeFalse($"a record at '{recordPath}' must never authenticate as '{principal}'");
    }

    /// <summary>
    /// A person cannot write into a service's token namespace by naming THEMSELVES as the node's main
    /// node (review of #7601): the own-scope shortcut keys on the path and the caller, and a
    /// caller-supplied <c>MainNode</c> must not stand in for either. Only the platform admin surface
    /// issues service tokens.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task APersonCannotWriteIntoAServiceTokenNamespace_ByNamingThemselvesMainNode()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetContext(Person(Owner));
        var answer = await NodeFactory.CreateNode(new MeshNode("forged", "Admin/_ServiceIdentity/svc-real/ApiToken")
            {
                Name = "forged", NodeType = ApiTokenNodeType.NodeType, MainNode = Owner,
                Content = new ApiToken
                {
                    TokenHash = new string('e', 64), UserId = "svc-real",
                    ServiceIdentityPath = "Admin/_ServiceIdentity/svc-real", Label = "forged",
                },
            })
            .Take(1)
            .Select(_ => "created")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(Budget).Emit("the create must answer", ct);
        Output.WriteLine($"service-namespace write as a person: {answer}");
        answer.Should().StartWith("refused:", "only the platform admin surface writes a service's tokens");
    }

    /// <summary>
    /// 🚨 THE LEGACY-INDEX PIN (review of #7601). An index that carries the token record itself has
    /// no separate location to bind to — it lives in the platform-only <c>ApiToken</c> partition — so
    /// it may never authenticate as System or as a multi-segment principal; an ordinary person's
    /// legacy token still does (the control).
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(WellKnownUsers.System, false)]
    // A case variant of System is System to the platform predicate (AccessService.IsPlatformPrincipal
    // compares case-insensitively), so it may not authenticate either (review of #7601, round 3).
    [InlineData("SYSTEM-SECURITY", false)]
    [InlineData("System-Security", false)]
    [InlineData(Owner + "/x", false)]
    [InlineData(Owner, true)]
    public async Task ALegacyInlineIndex_NeverAuthenticatesAsSystem(string principal, bool authenticates)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Mesh.JsonSerializerOptions;
        var index = new MeshNode(ApiTokenVerdict.IndexPath(Hash))
        {
            NodeType = ApiTokenNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(new ApiToken { TokenHash = Hash, UserId = principal }, options),
        };
        var response = await ApiTokenVerdict
            .Decide(RawToken, path => Observable.Return<MeshNode?>(path == index.Path ? index : null), options)
            .Should().Within(Budget).Emit("the verdict must answer", ct);
        response.Success.Should().Be(authenticates, $"a legacy inline index naming '{principal}'");
    }

    /// <summary>
    /// 🚨 THE MAIN-NODE PIN (review of #7601, round 2). A node's <c>MainNode</c> is what satellite
    /// delegation and the stored-main-node shortcut trust, so a write may only name a main node that
    /// IS the node or one of its ancestors. A comment in another person's partition naming the
    /// writer's own home as its main node would otherwise be authorised against the writer's home,
    /// not against where it lands. A main node that is an ancestor is the control.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AWriteNamesOnlyAnAncestorAsItsMainNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var rls = Mesh.ServiceProvider.GetServices<MeshWeaver.Mesh.Services.INodeValidator>()
            .OfType<MeshWeaver.Graph.Security.RlsNodeValidator>().Single();
        Task<MeshWeaver.Mesh.Services.NodeValidationResult> Validate(MeshNode node) =>
            rls.Validate(new MeshWeaver.Mesh.Services.NodeValidationContext
                {
                    Operation = MeshWeaver.Mesh.Security.NodeOperation.Create, Node = node, AccessContext = Person(Owner),
                })
                .FirstAsync().Timeout(Budget).Await(ct);

        var foreign = new MeshNode("c1", $"{Other}/Page/_Comment") { NodeType = "Comment", Name = "c1", MainNode = Owner };
        (await Validate(foreign)).IsValid.Should().BeFalse(
            "a node in another person's partition must not borrow the writer's home as its main node");

        var own = new MeshNode("c1", $"{Owner}/Page/_Comment") { NodeType = "Comment", Name = "c1", MainNode = $"{Owner}/Page" };
        (await Validate(own)).IsValid.Should().BeTrue("the control: an ancestor main node in the writer's own partition");
    }

    /// <summary>
    /// 🚨 THE CARRIED SERVICE-TOKEN PIN (review of #7601, round 4). A <c>DataChangeRequest</c> sent to a
    /// node the writer may update can carry ANOTHER node — here an existing service token, its hash
    /// replaced by one whose bearer the writer holds. The receiving workspace holds nothing under that
    /// key, so the commit point sees no predecessor and the service exception (the path is the
    /// service's own token namespace) would accept it. A token record or index is committed only by
    /// its own hub; carried anywhere else it is refused and the stored record is untouched. The
    /// writer's own carrier node, written through the same hub, is the control.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AServiceTokenCarriedThroughAnotherNodesHub_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        const string service = "svc-carried";
        var identityPath = ServiceIdentity.PathFor(service);
        var recordPath = $"{ServiceIdentity.TokenNamespace(service)}/t1";
        var carrierPath = $"{Owner}/Carrier";
        var original = new string('a', 64);
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("t1", ServiceIdentity.TokenNamespace(service))
            {
                Name = "t1", NodeType = ApiTokenNodeType.NodeType,
                Content = new ApiToken { TokenHash = original, UserId = service, ServiceIdentityPath = identityPath, Label = "t1" },
            }))
            .Should().Within(Budget).Emit("the service token must exist first", ct);
        await Access.RunAsSystem(() => NodeFactory.CreateNode(
                new MeshNode("Carrier", Owner) { Name = "Carrier", NodeType = "Markdown", MainNode = Owner }))
            .Should().Within(Budget).Emit("the carrier must exist first", ct);
        var record = await Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(recordPath)
                .Where(n => n is not null).Take(1))
            .Select(n => n!)
            .Should().Within(Budget).Emit("the service token must be readable", ct);

        var forged = record with { Content = TokenOf(record) with { TokenHash = Hash } };
        var response = await RequestHub
            .Observe<DataChangeResponse>(DataChangeRequest.Update([forged]),
                o => o.WithTarget(new Address(carrierPath)).WithAccessContext(Person(Owner)))
            .Select(d => d.Message.Status.ToString())
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name))
            .Should().Within(Budget).Emit("the carried write must answer", ct);
        Output.WriteLine($"carried service-token write: {response}");

        response.Should().NotBe(nameof(DataChangeStatus.Committed),
            "a token record carried through another node's hub never meets its own owner's rule");
        TokenOf(await Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(recordPath)
                    .Where(n => n is not null).Take(1))
                .Select(n => n!)
                .Should().Within(Budget).Emit("the service token must still be readable", ct))
            .TokenHash.Should().Be(original, "the carried hash never reached the stored record");
    }

    private Task<string> CreateAs(string principal, string nodeType, string id, CancellationToken ct) =>
        NodeFactory.CreateNode(new MeshNode(id, $"{Owner}/ApiToken")
            {
                Name = id, NodeType = nodeType, MainNode = Owner,
                Content = new ApiToken { TokenHash = new string('d', 64), UserId = principal, Label = id },
            })
            .Take(1)
            .Select(_ => "created")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(Budget).Emit("the create must answer", ct);
}
