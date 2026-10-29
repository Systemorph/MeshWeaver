using System.Reactive.Linq;
using System.Text.Json;
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
