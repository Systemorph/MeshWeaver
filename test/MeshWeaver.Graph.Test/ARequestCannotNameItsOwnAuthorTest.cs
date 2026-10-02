using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A client cannot choose the identity its write is authorised as, or the author it is
/// recorded under.</b>
///
/// <para><b>The defect (ingress audit for Plugins#2601).</b> Every client ingress (SignalR, gRPC,
/// the HTTP middleware) stamps the DELIVERY with the authenticated caller. The node-operation
/// messages also carry identity FIELDS: <c>CreateNodeRequest.CreatedBy</c>,
/// <c>DeleteNodeRequest.DeletedBy</c>, and the node's own <c>CreatedBy</c>/<c>LastModifiedBy</c>.
/// RLS read the request field BEFORE the delivery's context, and the create handler kept a
/// non-empty field. So a client that set <c>createdBy: "system-security"</c> in the message body
/// was authorised as System: it could create or delete anywhere, and the node it wrote named
/// System as its author, which is the identity the Store's control planes treat as
/// "the platform asked".</para>
///
/// <para><b>The rule.</b> When the delivery carries an authenticated principal (anybody who is
/// not the platform: not System and not a hub), THAT is the requester. A request field may only
/// name a different identity when the platform itself posted it. Internal writers that post as
/// System on behalf of someone else keep working, and no client can reach that branch, because
/// every ingress stamps the delivery.</para>
///
/// <para>Security fixture: <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, so RLS is real
/// and the caller holds nothing in the target space.</para>
/// </summary>
public class ARequestCannotNameItsOwnAuthorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Victim = "authorvictim";

    /// <summary>An authenticated identity with no grant anywhere: what an ingress stamps on the delivery.</summary>
    private static readonly AccessContext Mallory = new() { ObjectId = "mallory", Name = "Mallory" };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task SeedVictim() => SeedTopLevel(new MeshNode(Victim)
    {
        Name = "Victim space",
        NodeType = SpaceNodeType.NodeType,
        State = MeshNodeState.Active,
        Content = new Space(),
    });

    private static MeshNode Child(string id, string? author = null) => new(id, Victim)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        CreatedBy = author,
        LastModifiedBy = author,
    };

    /// <summary>Posts <paramref name="request"/> as an ingress does: the delivery stamped with <see cref="Mallory"/>.</summary>
    private Task<CreateNodeResponse> CreateAsMallory(CreateNodeRequest request) =>
        Access.RunAs(Mallory, () => ObserveNodeOperation(request, o => o.WithAccessContext(Mallory)))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit("the create must answer either way");

    /// <summary>The control: Mallory, asking as herself, is refused. Without it the exploit below proves nothing.</summary>
    [Fact]
    public async Task AUserWithoutRights_IsRefused()
    {
        await SeedVictim();

        var response = await CreateAsMallory(new CreateNodeRequest(Child("honest")));

        response.Success.Should().BeFalse("Mallory holds nothing in the victim space");
    }

    /// <summary>
    /// The exploit: the same request with <c>CreatedBy = system-security</c> in the message body.
    /// Before the fix RLS authorised it as System and the node landed.
    /// </summary>
    [Fact]
    public async Task ARequestNamingSystemAsItsAuthor_IsStillAuthorisedAsTheCaller()
    {
        await SeedVictim();

        var response = await CreateAsMallory(new CreateNodeRequest(Child("forged")) { CreatedBy = WellKnownUsers.System });

        response.Success.Should().BeFalse(
            "the delivery says Mallory; a CreatedBy field in the message body must not outrank it");
    }

    /// <summary>
    /// The forged node stamp: a caller who MAY create still cannot record somebody else, or System,
    /// as the node's author. Control planes read that stamp as "who asked" (Store's InvokerOf).
    /// </summary>
    [Fact]
    public async Task ANodeCarryingAForgedAuthor_IsRecordedUnderTheCaller()
    {
        await SeedMalloryHome();

        var node = new MeshNode("note", "mallory")
        {
            Name = "note",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            CreatedBy = WellKnownUsers.System,
            LastModifiedBy = WellKnownUsers.System,
        };
        var response = await CreateAsMallory(new CreateNodeRequest(node));

        response.Success.Should().BeTrue($"Mallory may create in her own home — {response.Error}");
        (response.Node?.CreatedBy).Should().Be("mallory", "the author is the authenticated caller, never a field she typed");
        (response.Node?.LastModifiedBy).Should().Be("mallory");
    }

    /// <summary>
    /// A move carries authorship by naming its stored source (<c>AuthorshipFrom</c>); a caller who
    /// could not move that source gets nothing from naming it — the stamps are its own.
    /// </summary>
    [Fact]
    public async Task NamingASourceTheCallerCannotMove_CarriesNoAuthorship()
    {
        await SeedVictim();
        await SeedMalloryHome();
        await Access.RunAsSystem(() => NodeFactory.CreateNode(Child("secret", "victim-owner")))
            .Should().Within(TestTimeouts.Convergence).Emit("the platform seeds a node Mallory cannot move");

        var response = await CreateAsMallory(new CreateNodeRequest(new MeshNode("borrowed", "mallory")
        {
            Name = "borrowed",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            CreatedBy = "victim-owner",
            LastModifiedBy = "victim-owner",
        })
        { AuthorshipFrom = $"{Victim}/secret" });

        response.Success.Should().BeTrue($"Mallory may create in her own home — {response.Error}");
        (response.Node?.CreatedBy).Should().Be("mallory",
            "naming a source she holds no Delete on carries none of its authorship");
    }

    /// <summary>The update path: a lambda that sets LastModifiedBy to System does not get to choose either.</summary>
    [Fact]
    public async Task AnUpdateNamingAnotherAuthor_IsRecordedUnderTheCaller()
    {
        await SeedMalloryHome();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var created = await Access.RunAs(Mallory, () => meshService.CreateNode(new MeshNode("draft", "mallory")
            {
                Name = "draft",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("Mallory may create in her own home");

        var updated = await Access.RunAs(Mallory, () => meshService.UpdateNode(created with
            {
                Name = "draft v2",
                LastModifiedBy = WellKnownUsers.System,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("Mallory may update her own node");

        updated.Name.Should().Be("draft v2");
        updated.LastModifiedBy.Should().Be("mallory", "the author of an update is the caller, never a value she set");
    }

    private async Task SeedMalloryHome()
    {
        await SeedTopLevel(new MeshNode("mallory")
        {
            Name = "Mallory's home",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Space(),
        });
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("mallory_Access", "mallory/_Access")
            {
                Name = "Mallory owns her home",
                NodeType = AccessAssignmentGuard.AccessAssignmentNodeType,
                MainNode = "mallory",
                State = MeshNodeState.Active,
                Content = new AccessAssignment
                {
                    AccessObject = "mallory",
                    Roles = [new RoleAssignment { Role = "Admin" }],
                },
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the platform grants Mallory her own home");
    }

    /// <summary>The platform posting on someone's behalf (delivery = System) keeps the field: the internal branch is unchanged.</summary>
    [Fact]
    public async Task ThePlatformPostingForSomeoneElse_KeepsTheNamedAuthor()
    {
        await SeedVictim();
        var system = new AccessContext { ObjectId = WellKnownUsers.System, Name = WellKnownUsers.System };

        var response = await Access.RunAs(system, () => ObserveNodeOperation(
                new CreateNodeRequest(Child("onbehalf", "jdoe")) { CreatedBy = WellKnownUsers.System },
                o => o.WithAccessContext(system)))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit("the create must answer either way");

        response.Success.Should().BeTrue($"the platform writes where it must — {response.Error}");
        (response.Node?.CreatedBy).Should().Be("jdoe", "an import or repair run by the platform preserves the recorded author");
    }
}
