using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Scoped API tokens (MeshWeaver.Plugins#4451): an optional <see cref="ApiToken.Scopes"/> list is a
/// CAP — a token that carries scopes can do at most what they allow, whatever its owner's grants
/// say — and a token without scopes behaves exactly as before.
///
/// <para>The motivating consumer is the device relay, which runs on a person's own machine and
/// holds a token only to keep a transport connection open. Scoped to <see cref="ApiTokenScope.Relay"/>
/// it must not read or write any node — not even in its owner's own partition, which the RLS
/// validator's own-scope shortcut otherwise grants without asking the permission fold.</para>
///
/// <para>Every seam a node permission is decided at is exercised: the permission fold
/// (<c>GetEffectivePermissions</c>), the RLS node validator (including its own-scope shortcut and
/// the token-record write rule) and a node type's own access rule
/// (<see cref="NodeTypeAccessRuleGate.Evaluate"/>). The unscoped arm is the negative control: the
/// same person, the same grants, the same calls — and the answers this suite proves the cap
/// changes are unchanged there.</para>
/// </summary>
public class ScopedApiTokenTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The person the token belongs to. Deliberately NOT the DevLogin harness admin.</summary>
    private const string TokenUser = "token-holder";

    /// <summary>A space where the token holder holds a real Editor grant.</summary>
    private const string GrantedPartition = "GrantedSpace";

    private static TimeSpan Budget => TestTimeouts.Quick;

    // ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every identity holds everything and the cap would be measured
    // against a permission set nobody actually has.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(GrantedPartition) { Name = "Granted Space", NodeType = "Markdown" },
                new MeshNode("Page", GrantedPartition) { Name = "Granted Page", NodeType = "Markdown" },
                new MeshNode("ScopeProbe", "AccessAssignment") { Name = "Scope Probe", NodeType = "Markdown" },
                new MeshNode(OtherUser) { Name = "Other User", NodeType = "User", MainNode = OtherUser },
                AssignmentNodeFactory.UserRole(TokenUser, "Editor", GrantedPartition));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>A Bearer request as the authenticating sites build it — <paramref name="scopes"/>
    /// straight from the token record (null = unscoped). Both contexts: the evaluator snapshots
    /// <c>Context ?? CircuitContext</c> on the caller's thread.</summary>
    private AccessContext BecomeToken(params string[]? scopes)
    {
        var ctx = new AccessContext
        {
            ObjectId = TokenUser,
            Name = "Token Holder",
            IsApiToken = true,
            Scopes = scopes,
        };
        Access.SetContext(ctx);
        Access.SetHostIdentity(ctx);
        return ctx;
    }

    private Task<Permission> Effective(string path, CancellationToken ct) =>
        Mesh.GetEffectivePermissions(path, TokenUser)
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);

    private RlsNodeValidator Rls => Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RlsNodeValidator>().Single();

    private Task<NodeValidationResult> Validate(AccessContext caller, NodeOperation operation, MeshNode node, CancellationToken ct) =>
        Rls.Validate(new NodeValidationContext { Operation = operation, Node = node, AccessContext = caller })
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);

    private static MeshNode GrantedPage => new("Page", GrantedPartition) { Name = "Granted Page", NodeType = "Markdown" };

    /// <summary>A node in the token holder's OWN partition — where the own-scope shortcut grants
    /// everything without asking the fold.</summary>
    private static MeshNode OwnNote => new("Note", TokenUser) { Name = "Note", NodeType = "Markdown", MainNode = TokenUser };

    private static MeshNode TokenRecord(string id, IReadOnlyList<string>? scopes) =>
        new(id, $"{TokenUser}/_Api")
        {
            NodeType = ApiTokenNodeType.NodeType,
            MainNode = TokenUser,
            Content = new ApiToken { TokenHash = new string('a', 64), UserId = TokenUser, Label = id, Scopes = scopes },
        };

    // ── Refused outside its scopes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A relay-only token holds NOTHING on a node its owner may edit: the fold is capped to the
    /// node scopes it carries, and <c>relay</c> carries none.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RelayToken_HoldsNoNodePermission_WhereItsOwnerIsEditor()
    {
        var ct = TestContext.Current.CancellationToken;
        BecomeToken(ApiTokenScope.Relay);

        (await Effective($"{GrantedPartition}/Page", ct)).Should().Be(Permission.None,
            "a token scoped to the relay is capped to no node permission at all");
    }

    /// <summary>
    /// 🚨 THE SHORTCUT PIN. In its owner's OWN partition the RLS validator grants every operation
    /// without asking the fold; a cap only inside the fold would leave a relay token free to read
    /// and rewrite its owner's whole home. Refused BY NAME — the message lists the token's scopes
    /// and the scope the operation needs.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RelayToken_IsRefusedInItsOwnersOwnPartition_ByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = BecomeToken(ApiTokenScope.Relay);

        var read = await Validate(caller, NodeOperation.Read, OwnNote, ct);
        read.IsValid.Should().BeFalse("the own-scope shortcut must not lift the scope cap");
        read.Reason.Should().Be(NodeRejectionReason.Unauthorized);
        read.ErrorMessage.Should().Contain("'relay'").And.Contain("'read'");

        var update = await Validate(caller, NodeOperation.Update, OwnNote, ct);
        update.IsValid.Should().BeFalse();
        update.ErrorMessage.Should().Contain("'write'");
    }

    /// <summary>A read-only token cannot write where its owner is Editor.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ReadToken_CannotWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = BecomeToken(ApiTokenScope.Read);

        (await Effective($"{GrantedPartition}/Page", ct)).HasFlag(Permission.Update).Should().BeFalse(
            "the read scope caps Update out, although the owner is Editor");
        (await Validate(caller, NodeOperation.Update, GrantedPage, ct)).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// A node type's own access rule may GRANT where the fold would not — and a capped token must
    /// not reach through that grant. A rule that would grant everything is never even asked.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ATypeRuleThatGrants_DoesNotLiftTheCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = BecomeToken(ApiTokenScope.Relay);
        var rule = new GrantsEverything();

        var outcome = await NodeTypeAccessRuleGate
            .Evaluate(rule, new NodeValidationContext { Operation = NodeOperation.Read, Node = GrantedPage, AccessContext = relay }, TokenUser)
            .FirstAsync().Timeout(Budget).Await(ct);

        outcome.IsGranted.Should().BeFalse("the scope cap is a fact about the token, decided before any rule");
        rule.Asked.Should().BeFalse("a capped operation never reaches the rule");

        // Positive control: the same rule, a token whose scopes DO allow the read.
        var reader = BecomeToken(ApiTokenScope.Read);
        (await NodeTypeAccessRuleGate
            .Evaluate(rule, new NodeValidationContext { Operation = NodeOperation.Read, Node = GrantedPage, AccessContext = reader }, TokenUser)
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken))
            .IsGranted.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 THE QUERY PIN. A listing does not go through the fold the cap lives in: SQL-backed
    /// providers filter rows in the database, and the in-process providers rebuild the row check's
    /// context from the viewer's id alone. So a token whose scopes allow no read gets NOTHING from
    /// a query — decided once at the query boundary — while a read-scoped token and an unscoped one
    /// see exactly the rows their owner may read.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AQuery_ReturnsNothingToATokenWithoutRead_AndRowsToOneWithIt()
    {
        var ct = TestContext.Current.CancellationToken;

        BecomeToken(ApiTokenScope.Relay);
        (await Children(ct)).Should().BeEmpty("a token scoped to the relay reads nothing — not even through a listing");

        BecomeToken(ApiTokenScope.Read);
        (await Children(ct)).Should().Contain($"{GrantedPartition}/Page", "the read scope reads what its owner may read");

        BecomeToken(null);
        (await Children(ct)).Should().Contain($"{GrantedPartition}/Page", "an unscoped token is unchanged");
    }

    private Task<string[]> Children(CancellationToken ct) =>
        Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{GrantedPartition} scope:children"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(change => change.Items.Select(n => n.Path ?? "").ToArray())
            .Timeout(Budget)
            .Await(ct);

    // ── Allowed inside its scopes ───────────────────────────────────────────────────────────────

    /// <summary>The same token is allowed what its scopes cover: read with <c>read</c>, write with
    /// <c>write</c> — capped, never widened past the owner's grant.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ScopedToken_IsAllowedInsideItsScopes()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = BecomeToken(ApiTokenScope.Read);
        (await Effective($"{GrantedPartition}/Page", ct)).HasFlag(Permission.Read).Should().BeTrue();
        (await Validate(reader, NodeOperation.Read, OwnNote, ct)).IsValid.Should().BeTrue();
        (await Validate(reader, NodeOperation.Read, GrantedPage, ct)).IsValid.Should().BeTrue();

        var editor = BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write);
        var perms = await Effective($"{GrantedPartition}/Page", ct);
        perms.HasFlag(Permission.Read).Should().BeTrue();
        perms.HasFlag(Permission.Update).Should().BeTrue();
        (await Validate(editor, NodeOperation.Update, OwnNote, ct)).IsValid.Should().BeTrue();
    }

    /// <summary>The cap never WIDENS: a write-scoped token holds nothing where its owner holds nothing.</summary>
    [Fact(Timeout = 60_000)]
    public async Task TheCap_NeverGrants()
    {
        var ct = TestContext.Current.CancellationToken;
        BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write);

        (await Effective("ForeignSpace/Page", ct)).Should().Be(Permission.None,
            "a scope caps what the owner already holds — it never grants");
    }

    // ── Unscoped: the negative control ──────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL. A token with no scopes — null, as every token minted before scopes
    /// existed, or an empty list — answers exactly as an unscoped token always did: its owner's
    /// Editor grant, and the own-scope shortcut, untouched.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnscopedToken_IsUnchanged(bool emptyList)
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = emptyList ? BecomeToken([]) : BecomeToken(null);

        var perms = await Effective($"{GrantedPartition}/Page", ct);
        perms.HasFlag(Permission.Read).Should().BeTrue();
        perms.HasFlag(Permission.Update).Should().BeTrue();
        (await Validate(caller, NodeOperation.Update, OwnNote, ct)).IsValid.Should().BeTrue();
        (await Validate(caller, NodeOperation.Update, GrantedPage, ct)).IsValid.Should().BeTrue();
        (await Validate(caller, NodeOperation.Create, TokenRecord("unscoped-mint", null), ct)).IsValid.Should().BeTrue(
            "an unscoped token mints tokens exactly as before");
    }

    // ── Unknown scope: refused and named, never "all" ───────────────────────────────────────────

    /// <summary>
    /// 🚨 A value no enforcement site claims grants NOTHING — it is never read as "unscoped" or
    /// "all" — and the refusal names it, so the holder can see why.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task UnknownScope_IsRefusedAndNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = BecomeToken("bogus");

        (await Effective($"{GrantedPartition}/Page", ct)).Should().Be(Permission.None);

        var read = await Validate(caller, NodeOperation.Read, OwnNote, ct);
        read.IsValid.Should().BeFalse("an unknown scope is not a licence for everything");
        read.ErrorMessage.Should().Contain("'bogus'");

        ApiTokenScope.Refusal(caller, ApiTokenScope.Mcp).Should().Contain("'bogus'").And.Contain("'mcp'");
        ApiTokenScope.Allows(caller, ApiTokenScope.Mcp).Should().BeFalse();
    }

    // ── A capped token cannot widen itself ──────────────────────────────────────────────────────

    /// <summary>
    /// A scoped token holding <c>write</c> must not mint an unscoped token, nor one wider than
    /// itself, nor rewrite its own record's scopes away — otherwise the cap is one write deep.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ScopedToken_CannotMintAWiderToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var writerWithoutTokens = BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write);
        var noTokensScope = await Validate(writerWithoutTokens, NodeOperation.Update, TokenRecord("self", [ApiTokenScope.Read]), ct);
        noTokensScope.IsValid.Should().BeFalse("writing a token record needs the tokens scope");
        noTokensScope.ErrorMessage.Should().Contain("'tokens'");

        var minter = BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write, ApiTokenScope.Tokens);
        (await Validate(minter, NodeOperation.Create, TokenRecord("unscoped", null), ct)).IsValid.Should().BeFalse(
            "a capped token cannot mint a full-authority one");
        var wider = await Validate(minter, NodeOperation.Create, TokenRecord("wider", [ApiTokenScope.Read, ApiTokenScope.Mcp]), ct);
        wider.IsValid.Should().BeFalse();
        wider.ErrorMessage.Should().Contain("'mcp'");

        (await Validate(minter, NodeOperation.Create, TokenRecord("narrower", [ApiTokenScope.Read]), ct)).IsValid.Should().BeTrue(
            "a subset of the minter's own scopes is allowed");
    }

    /// <summary>
    /// 🚨 THE ROUTED-UPDATE PIN (review of #7585). The canonical write — <c>GetMeshNodeStream(path).Update</c>
    /// — never runs the RLS validator: it is an owner-enforced validator, and the owner decides the
    /// patch at its delivery gate from the (capped) fold alone. A token holding <c>write</c> holds
    /// Update on its owner's own records, so without a rule at that gate it could strip its own
    /// record's scopes — or retype the record so no token-record rule recognises it, while the
    /// verdict still authenticates its content — and be unscoped on its next request.
    /// The unscoped arm is the control: the same write by the same person lands.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AScopedToken_CannotRewriteItsOwnRecord_ThroughTheRoutedUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TokenUser}/_Api/self";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(TokenRecord("self", [ApiTokenScope.Read, ApiTokenScope.Write])))
            .Should().Within(TestTimeouts.Convergence).Emit("the token record must exist first", ct);

        BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write);
        (await RoutedUpdate(path, n => n with { Content = TokenOf(n) with { Scopes = null } }, ct))
            .Should().StartWith("refused:", "a capped token must not strip its own record's scopes");
        (await RoutedUpdate(path, n => n with { NodeType = "Markdown" }, ct))
            .Should().StartWith("refused:", "nor retype its record out of the token-record rule's sight");

        var after = await StoredRecord(path, ct);
        after.NodeType.Should().Be(ApiTokenNodeType.NodeType);
        TokenOf(after).Scopes.Should().Equal(ApiTokenScope.Read, ApiTokenScope.Write);

        // The control: an unscoped token (its owner's full authority) writes the same record.
        BecomeToken(null);
        (await RoutedUpdate(path, n => n with { Content = TokenOf(n) with { Label = "renamed" } }, ct))
            .Should().Be("landed", "the record is writable by its owner — the refusal above is the cap, not the path");
    }

    /// <summary>
    /// 🚨 THE OWN-STREAM PIN. The same rewrite, issued on the record's OWN hub through
    /// <c>GetMeshNodeStream().Update</c> — the route code running on the owner takes (a rollback
    /// request's handler, an activity script, a layout-area action). That write never reached the
    /// commit point, so the token-record rule was skipped and a capped token could strip its own
    /// scopes. The unscoped arm is the control: the owner's own write on the same route lands.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AScopedToken_CannotRewriteItsOwnRecord_ThroughTheOwnersOwnStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TokenUser}/_Api/ownstream";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(TokenRecord("ownstream", [ApiTokenScope.Read, ApiTokenScope.Write])))
            .Should().Within(TestTimeouts.Convergence).Emit("the token record must exist first", ct);
        await StoredRecord(path, ct);

        BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write);
        (await OwnStreamUpdate(path, n => n with { Content = TokenOf(n) with { Scopes = null } }, ct))
            .Should().StartWith("refused:", "a capped token must not strip its own record's scopes on the owner's own stream");
        TokenOf(await StoredRecord(path, ct)).Scopes.Should().Equal(ApiTokenScope.Read, ApiTokenScope.Write);

        BecomeToken(null);
        (await OwnStreamUpdate(path, n => n with { Content = TokenOf(n) with { Label = "renamed" } }, ct))
            .Should().Be("landed", "the control: the owner's own write on the same route lands");
    }

    /// <summary>
    /// 🚨 THE RETYPE-INTO PIN (review of #7585, round 3). A scoped token holding <c>tokens</c> may
    /// delete a token it minted, and may create an ordinary node. Recreating the record's path as
    /// Markdown and then retyping it INTO <c>ApiToken</c> with unscoped content through the routed
    /// update would leave the surviving index authenticating an unscoped credential. The gate sees
    /// only the Markdown it holds, so the rule is judged at the commit point against the merged
    /// result. The unscoped arm is the control: the owner's own retype lands.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AScopedToken_CannotRetypeANodeIntoATokenRecord_ThroughTheRoutedUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TokenUser}/_Api/recreated";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(
                new MeshNode("recreated", $"{TokenUser}/_Api") { Name = "recreated", NodeType = "Markdown", MainNode = TokenUser }))
            .Should().Within(TestTimeouts.Convergence).Emit("the lookalike must exist first", ct);
        MeshNode IntoUnscopedToken(MeshNode n) => n with
        {
            NodeType = ApiTokenNodeType.NodeType,
            Content = new ApiToken { TokenHash = new string('b', 64), UserId = TokenUser, Label = "recreated", Scopes = null },
        };

        BecomeToken(ApiTokenScope.Api, ApiTokenScope.Read, ApiTokenScope.Write, ApiTokenScope.Tokens);
        (await RoutedUpdate(path, IntoUnscopedToken, ct)).Should().StartWith("refused:",
            "a scoped token must not turn an ordinary node into an unscoped token record");
        (await StoredRecord(path, ct)).NodeType.Should().Be("Markdown", "the refused retype committed nothing");

        BecomeToken(null);
        (await RoutedUpdate(path, IntoUnscopedToken, ct)).Should().Be("landed",
            "the control: the same retype by the owner's full authority lands — the refusal above is the cap");
    }

    /// <summary>
    /// 🚨 THE CARRIED-RECORD PIN (review of #7585, round 4). A <c>DataChangeRequest</c> sent to ANOTHER
    /// writable node's hub can carry an existing token record — scopes stripped — whose own owner never
    /// sees the write: the receiving workspace holds nothing under that key, so the commit point sees
    /// no predecessor. A token record is committed only by its own hub, as a create there; carried
    /// anywhere else it is refused.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ATokenRecordCarriedThroughAnotherNodesHub_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var recordPath = $"{TokenUser}/_Api/carried";
        var carrierPath = $"{TokenUser}/Carrier";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(TokenRecord("carried", [ApiTokenScope.Read])))
            .Should().Within(TestTimeouts.Convergence).Emit("the token record must exist first", ct);
        await Access.RunAsSystem(() => NodeFactory.CreateNode(
                new MeshNode("Carrier", TokenUser) { Name = "Carrier", NodeType = "Markdown", MainNode = TokenUser }))
            .Should().Within(TestTimeouts.Convergence).Emit("the carrier must exist first", ct);
        var record = await StoredRecord(recordPath, ct);

        var scoped = BecomeToken(ApiTokenScope.Api, ApiTokenScope.Read, ApiTokenScope.Write, ApiTokenScope.Tokens);
        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([record with { Content = TokenOf(record) with { Scopes = null } }]),
                o => o.WithTarget(new Address(carrierPath)).WithAccessContext(scoped))
            .Select(d => d.Message.Status.ToString())
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name))
            .Should().Within(TestTimeouts.Convergence).Emit("the carried write must answer", ct);
        Output.WriteLine($"carried token write: {response}");

        response.Should().NotBe(nameof(DataChangeStatus.Committed),
            "a token record carried through another node's hub never meets its own owner's rule");
        TokenOf(await StoredRecord(recordPath, ct)).Scopes.Should().Equal(ApiTokenScope.Read);
    }

    /// <summary>
    /// 🚨 THE TYPE-PATH PIN (review of #7585). The stream cache passes a path whose first segment
    /// names a satellite type (<c>AccessAssignment/…</c>, <c>Thread/…</c>) straight to its shared,
    /// privileged upstream without asking the fold — so the cap has to be applied ahead of that
    /// shortcut, or a token scoped to no read still reads those nodes.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ATokenWithoutRead_CannotReadATypeNamedPath_ThroughTheStreamCache()
    {
        var ct = TestContext.Current.CancellationToken;

        BecomeToken(ApiTokenScope.Relay);
        (await StreamRead(TypeNamedPath, ct)).Should().StartWith("denied:",
            "a token scoped to the relay reads no node — the type-path shortcut included");

        BecomeToken(ApiTokenScope.Read);
        (await StreamRead(TypeNamedPath, ct)).Should().Be("read:" + TypeNamedPath,
            "the control: a read-scoped token is served the path exactly as before");
    }

    private const string TypeNamedPath = "AccessAssignment/ScopeProbe";

    /// <summary>Another person's User node — public-read to every signed-in user by a HUB-level rule.</summary>
    private const string OtherUser = "other-user";

    /// <summary>
    /// 🚨 THE HUB-RULE PIN (review of #7585). A User hub grants Read to every signed-in caller through
    /// a hub-level rule, which the delivery gate used to accept before any fold ran — so the cap never
    /// saw a routed read there. A relay token must be refused; a read-scoped one is the control.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AHubLevelReadGrant_DoesNotLiftTheCap_OnARoutedRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = BecomeToken(ApiTokenScope.Relay);
        (await RoutedRead(OtherUser, relay, ct)).Should().StartWith("refused:",
            "a relay token reads no node, whatever a hub-level rule grants signed-in users");

        var reader = BecomeToken(ApiTokenScope.Read);
        (await RoutedRead(OtherUser, reader, ct)).Should().Be("served",
            "the control: a read-scoped token is served the public profile exactly as before");
    }

    private Task<string> RoutedRead(string path, AccessContext identity, CancellationToken ct) =>
        RequestHub
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(path)).WithAccessContext(identity))
            .Take(1)
            .Select(_ => "served")
            .Catch((DeliveryFailureException ex) => Observable.Return("refused:" + ex.Failure.ErrorType + ": " + ex.Failure.Message))
            .Should().Within(TestTimeouts.Convergence).Emit("the routed read must answer", ct);

    /// <summary>
    /// 🚨 THE RECORD-TYPE PIN (review of #7585). The hash index outlives its record, so a record
    /// deleted and recreated at the same path as another type — carrying ApiToken-shaped content with
    /// no scopes — must not authenticate: only a token record does. A legacy index that carries the
    /// record itself, and a token record, still authenticate (the controls).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnlyATokenRecord_Authenticates_NotARecreatedLookalike()
    {
        var ct = TestContext.Current.CancellationToken;
        const string raw = "mw_scoped-token-lookalike";
        var hash = ValidateTokenRequest.HashToken(raw);
        var options = Mesh.JsonSerializerOptions;
        const string recordPath = TokenUser + "/_Api/lookalike";
        var index = new MeshNode(ApiTokenVerdict.IndexPath(hash))
        {
            NodeType = ApiTokenNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(new ApiTokenIndex { TokenHash = hash, TokenPath = recordPath }, options),
        };
        MeshNode Record(string nodeType) => MeshNode.FromPath(recordPath) with
        {
            NodeType = nodeType,
            Content = JsonSerializer.SerializeToElement(new ApiToken { TokenHash = hash, UserId = TokenUser }, options),
        };
        Task<ValidateTokenResponse> Decide(MeshNode record) => ApiTokenVerdict
            .Decide(raw, path => Observable.Return<MeshNode?>(path == index.Path ? index : path == recordPath ? record : null), options)
            .FirstAsync().Timeout(Budget).Await(ct);

        (await Decide(Record("Markdown"))).Success.Should().BeFalse(
            "a recreated non-token node with token-shaped content must not authenticate");
        (await Decide(Record(null!))).Success.Should().BeFalse(
            "nor an UNTYPED node at the indexed path — a content-only create bypasses every type-based write rule");
        (await Decide(Record(""))).Success.Should().BeFalse("nor an empty-typed one");
        (await Decide(Record(ApiTokenNodeType.NodeType))).Success.Should().BeTrue("the control: a token record does");
    }

    private Task<string> StreamRead(string path, CancellationToken ct) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Take(1)
            .Select(n => "read:" + n!.Path)
            .Catch((UnauthorizedAccessException ex) => Observable.Return("denied:" + ex.Message))
            .Should().Within(TestTimeouts.Convergence).Emit("the read must answer", ct);

    private ApiToken TokenOf(MeshNode node) => node.ContentAs<ApiToken>(Mesh.JsonSerializerOptions)!;

    private Task<string> RoutedUpdate(string path, Func<MeshNode, MeshNode> change, CancellationToken ct) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(change)
            .Take(1)
            .Select(_ => "landed")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(TestTimeouts.Convergence).Emit("the update must answer", ct);

    /// <summary>A write on the node's OWN hub, through its own stream, as the current identity.</summary>
    private Task<string> OwnStreamUpdate(string path, Func<MeshNode, MeshNode> change, CancellationToken ct)
    {
        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
                    ?? throw new InvalidOperationException($"the owning hub of {path} is not active");
        return owner.GetWorkspace().GetMeshNodeStream()
            .Update(change)
            .Take(1)
            .Select(_ => "landed")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(TestTimeouts.Convergence).Emit("the own write must answer", ct);
    }

    private Task<MeshNode> StoredRecord(string path, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null).Take(1))
            .Select(n => n!)
            .Should().Within(TestTimeouts.Convergence).Emit("the record must be readable", ct);

    /// <summary>
    /// 🚨 THE PRINCIPAL PIN (review of #7585, round 5). The subset rule bounds the scopes a scoped
    /// token mints, and must also bind WHOSE token it mints: a record naming another principal —
    /// System above all, which the fold and RLS admit before any cap — or a service identity would
    /// authenticate as that principal. A scoped minter writes only its own principal's tokens.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AScopedToken_MintsOnlyForItsOwnPrincipal()
    {
        var ct = TestContext.Current.CancellationToken;
        var minter = BecomeToken(ApiTokenScope.Read, ApiTokenScope.Write, ApiTokenScope.Tokens);
        MeshNode Minted(string userId, string? serviceIdentityPath = null) => TokenRecord("bound", [ApiTokenScope.Read]) with
        {
            Content = new ApiToken
            {
                TokenHash = new string('c', 64), UserId = userId, Label = "bound",
                Scopes = [ApiTokenScope.Read], ServiceIdentityPath = serviceIdentityPath,
            },
        };

        (await Validate(minter, NodeOperation.Create, Minted(WellKnownUsers.System), ct)).IsValid.Should().BeFalse(
            "a scoped token must not mint a System credential, however narrow its scopes");
        (await Validate(minter, NodeOperation.Create, Minted("someone-else"), ct)).IsValid.Should().BeFalse(
            "nor one for another person");
        (await Validate(minter, NodeOperation.Create, Minted(TokenUser, "Svc/Identity"), ct)).IsValid.Should().BeFalse(
            "nor a service credential");
        (await Validate(minter, NodeOperation.Create, Minted(TokenUser), ct)).IsValid.Should().BeTrue(
            "the control: its own principal, a subset of its scopes");
    }

    // ── The verdict carries the cap ─────────────────────────────────────────────────────────────

    /// <summary>The shared verdict returns the token's scopes, and null for an unscoped token — so
    /// every authenticating site has the cap to stamp.</summary>
    [Fact(Timeout = 60_000)]
    public async Task TheVerdict_CarriesTheScopes()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Verdict([ApiTokenScope.Relay], ct)).Scopes.Should().Equal(ApiTokenScope.Relay);
        (await Verdict(null, ct)).Scopes.Should().BeNull();
        (await Verdict([], ct)).Scopes.Should().BeNull("an empty list is unscoped, and travels as such");
    }

    private async Task<ValidateTokenResponse> Verdict(IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        const string raw = "mw_scoped-token-test";
        var hash = ValidateTokenRequest.HashToken(raw);
        var options = Mesh.JsonSerializerOptions;
        var record = new MeshNode(ApiTokenVerdict.IndexPath(hash))
        {
            NodeType = ApiTokenNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(new ApiToken
            {
                TokenHash = hash, UserId = TokenUser, UserName = "Token Holder", UserEmail = "t@example.com", Scopes = scopes,
            }, options),
        };
        var response = await ApiTokenVerdict
            .Decide(raw, path => Observable.Return<MeshNode?>(path == record.Path ? record : null), options)
            .FirstAsync().Timeout(Budget).Await(ct);
        response.Success.Should().BeTrue(response.Error ?? "");
        return response;
    }

    private sealed class GrantsEverything : INodeTypeAccessRule
    {
        public bool Asked { get; private set; }
        public string NodeType => "Markdown";
        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            Asked = true;
            return Observable.Return(true);
        }
    }
}
