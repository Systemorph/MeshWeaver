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
