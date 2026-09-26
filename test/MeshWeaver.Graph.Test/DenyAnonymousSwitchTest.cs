using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The instance-level anonymous switch, <c>Access:DenyAnonymous</c> (<see cref="AnonymousAccess"/>),
/// measured against a real monolith mesh carrying every shape that opens a path to a logged-out
/// caller — an Anonymous Viewer grant (what installed packages write), a
/// <c>PartitionAccessPolicy.PublicRead</c> policy, and a Public-only grant (the signed-in baseline).
///
/// <para>The two concrete classes differ ONLY in whether the switch is stated, so every "on" reading
/// has its "off" control in <see cref="DenyAnonymousSwitchOffTest"/>: the grants really do open
/// those paths, and the refusal is the switch's doing. See
/// <c>Doc/Architecture/AccessControl</c> → "Closing an instance to anonymous callers".</para>
/// </summary>
public abstract class DenyAnonymousSwitchTestBase(ITestOutputHelper output, bool denyAnonymous)
    : MonolithMeshTestBase(output)
{
    /// <summary>A signed-in user holding no grant of their own anywhere below.</summary>
    protected const string SignedIn = "deny-anonymous-signed-in";

    /// <summary>A signed-in user holding a real Viewer grant on <c>Owned</c>.</summary>
    protected const string Owner = "deny-anonymous-owner";

    /// <summary>Whether this class states <c>Access:DenyAnonymous = true</c>.</summary>
    protected bool DenyAnonymous { get; } = denyAnonymous;

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin,
    // which would make every reading below Read regardless of the switch.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                // What a package install writes: Anonymous + Public Viewer at the partition root.
                new MeshNode("AnonOpen") { NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "AnonOpen"),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "AnonOpen"),
                new MeshNode("Page", "AnonOpen") { NodeType = "Markdown" },

                // A fully-public partition: the policy grant, no role at all.
                new MeshNode("PolicyOpen") { NodeType = "Markdown" },
                AssignmentNodeFactory.Policy("PolicyOpen", new PartitionAccessPolicy { PublicRead = true }),
                new MeshNode("Page", "PolicyOpen") { NodeType = "Markdown" },

                // Signed-in only: Public Viewer, no Anonymous grant.
                new MeshNode("SignedInOnly") { NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "SignedInOnly"),
                new MeshNode("Page", "SignedInOnly") { NodeType = "Markdown" },

                // A user's own grant.
                new MeshNode("Owned") { NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(Owner, "Viewer", "Owned"),
                new MeshNode("Page", "Owned") { NodeType = "Markdown" })
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(
                    ImmutableDictionary<string, string?>.Empty.Add(
                        AnonymousAccess.DenyAnonymousConfigKey, DenyAnonymous ? "true" : null)).Build()));

    /// <summary>
    /// The effective permission a SUBJECT holds on a path — the FIRST decision, so an initial leak
    /// cannot hide behind a later matching answer.
    /// </summary>
    protected Task<Permission> Effective(string path, string subject, CancellationToken cancellationToken)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var context = new AccessContext { ObjectId = subject, Name = subject };
        access.SetContext(context);
        access.SetHostIdentity(context);
        return Mesh.GetEffectivePermissions(path, subject)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(cancellationToken);
    }

    /// <summary>True when <paramref name="subject"/> may READ <paramref name="path"/>.</summary>
    protected async Task<bool> CanRead(string path, string subject, CancellationToken cancellationToken)
    {
        var permission = await Effective(path, subject, cancellationToken);
        Output.WriteLine($"[deny={DenyAnonymous}] {subject} on {path}: {permission}");
        return permission.HasFlag(Permission.Read);
    }

    /// <summary>The Initial items of a query run as <paramref name="viewer"/> ("" = the anonymous visitor).</summary>
    protected Task<IReadOnlyList<MeshNode>> QueryAs(string query, string viewer, CancellationToken cancellationToken)
        => MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query, viewer))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .FirstAsync().Timeout(TestTimeouts.Quick)
            .Await(cancellationToken);

    /// <summary>A logged-out visitor's circuit: VIRTUAL, and named after its guest id, not Anonymous.</summary>
    protected const string Guest = "guest-deny-anonymous";

    /// <summary>Installs <paramref name="context"/> as the ambient caller.</summary>
    protected void ActAs(AccessContext context)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(context);
        access.SetHostIdentity(context);
    }

    /// <summary>The virtual (logged-out) visitor context.</summary>
    protected static AccessContext VirtualGuest(string? locale = null)
        => new() { ObjectId = Guest, Name = Guest, IsVirtual = true, Locale = locale };

    /// <summary>
    /// Evaluates <paramref name="subject"/> with the SAME id as the ambient context — the shape
    /// MeshNodeStreamCache and the delivery gate use (<c>GetEffectivePermissions(path, captured.ObjectId)</c>).
    /// </summary>
    protected Task<Permission> EffectiveUnder(AccessContext ambient, string path, CancellationToken cancellationToken)
    {
        ActAs(ambient);
        return Mesh.GetEffectivePermissions(path, ambient.ObjectId!)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(cancellationToken);
    }

    /// <summary>The exact-node read through the process-wide cache, as the ambient caller.</summary>
    protected Task<MeshNode> ExactRead(AccessContext ambient, string path, CancellationToken cancellationToken)
    {
        ActAs(ambient);
        return Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>()
            .GetStream(path, Mesh.JsonSerializerOptions)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(cancellationToken);
    }

    /// <summary>The RLS node validator's verdict on a READ of <paramref name="path"/> by <paramref name="caller"/>.</summary>
    protected Task<NodeValidationResult> RlsRead(AccessContext caller, string path, CancellationToken cancellationToken)
    {
        var slash = path.LastIndexOf('/');
        var node = slash < 0 ? new MeshNode(path) : new MeshNode(path[(slash + 1)..], path[..slash]);
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RlsNodeValidator>().Single();
        return validator.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = node with { NodeType = "Markdown" },
                AccessContext = caller,
            })
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(cancellationToken);
    }

    /// <summary>
    /// Signed-in reads are identical with the switch on and off: a user's own grant, and the Public
    /// grants every signed-in user inherits (the Public leg of the fold enters BELOW the switch).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SignedInReads_AreUnaffected()
    {
        var ct = TestContext.Current.CancellationToken;

        (await CanRead("Owned/Page", Owner, ct)).Should().BeTrue("a user's own Viewer grant");
        (await CanRead("SignedInOnly/Page", SignedIn, ct)).Should().BeTrue(
            "Public is the baseline of every SIGNED-IN user, never a logged-out caller — the switch "
            + "must not take it away");
        (await CanRead("AnonOpen/Page", SignedIn, ct)).Should().BeTrue(
            "the Public half of a package's grant pair keeps serving signed-in users");
        (await CanRead("PolicyOpen/Page", SignedIn, ct)).Should().BeTrue(
            "a PublicRead policy keeps granting signed-in users");
        (await CanRead("Owned/Page", SignedIn, ct)).Should().BeFalse(
            "the control: a path nothing grants to this user stays closed");
    }

    /// <summary>The system identity is never anonymous and is never refused.</summary>
    [Fact(Timeout = 60_000)]
    public async Task SystemIdentity_IsUnaffected()
    {
        var ct = TestContext.Current.CancellationToken;
        (await CanRead("Owned/Page", WellKnownUsers.System, ct)).Should().BeTrue();
    }
}

/// <summary>
/// <c>Access:DenyAnonymous = true</c>: the anonymous subject holds nothing anywhere, whatever the
/// mesh grants it.
/// </summary>
public class DenyAnonymousSwitchTest(ITestOutputHelper output) : DenyAnonymousSwitchTestBase(output, denyAnonymous: true)
{
    /// <summary>
    /// 🚨 THE REQUIREMENT. An anonymous read of a node carrying an explicit Anonymous Viewer grant —
    /// exactly what an installed package writes — is refused, and so is a node under a PublicRead
    /// policy. The off-class reads both as granted, so this is the switch and not the model.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData("AnonOpen/Page")]
    [InlineData("AnonOpen")]
    [InlineData("PolicyOpen/Page")]
    [InlineData("SignedInOnly/Page")]
    public async Task AnonymousRead_IsRefused_WhateverTheGrants(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        (await Effective(path, WellKnownUsers.Anonymous, ct)).Should().Be(Permission.None,
            "Access:DenyAnonymous resolves every check for the anonymous subject to no access, above "
            + "every _Access grant, PublicRead policy and gate surface");
        (await Effective(path, "", ct)).Should().Be(Permission.None,
            "an empty identity IS the anonymous subject and must not slip past the switch");
    }

    /// <summary>
    /// The anonymous navigation gate — the instrument behind page loads, the SEO head, the sitemap
    /// and content routes — answers a definitive Denied (so the visitor is sent to sign in), never an
    /// undetermined outcome.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnonymousGate_Denies()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await AnonymousGate.Evaluate(Mesh, "AnonOpen/Page")
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct);
        outcome.IsGranted.Should().BeFalse();
        outcome.IsUndetermined.Should().BeFalse("the switch is a verdict, not a degraded fold");
    }

    /// <summary>
    /// A logged-out caller's QUERY answers nothing (the SQL providers never consult the C#
    /// evaluator, so the read boundary refuses it too), while the same query for a signed-in user
    /// returns the node.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnonymousQuery_AnswersNothing_SignedInQuery_Unaffected()
    {
        var ct = TestContext.Current.CancellationToken;

        (await QueryAs("path:AnonOpen/Page", "", ct)).Should().BeEmpty(
            "a query resolved to the anonymous viewer is refused at the MeshService read boundary");
        (await QueryAs("path:AnonOpen/Page", WellKnownUsers.Anonymous, ct)).Should().BeEmpty();
        (await QueryAs("path:AnonOpen/Page", SignedIn, ct)).Should().ContainSingle(
            n => n.Path == "AnonOpen/Page", "a signed-in viewer's query is not touched by the switch");
    }

    /// <summary>
    /// A VIRTUAL caller names itself after its guest id, not Anonymous — and the exact-node read path
    /// (MeshNodeStreamCache) and the delivery gate pass that id straight through. It is still a
    /// logged-out caller, so the switch refuses it, both in the fold and on the cached exact read.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task VirtualCaller_IsRefused_InTheFoldAndOnTheExactRead()
    {
        var ct = TestContext.Current.CancellationToken;
        (await EffectiveUnder(VirtualGuest(), "AnonOpen/Page", ct)).Should().Be(Permission.None,
            "a virtual ambient context is a logged-out visitor whatever id it carries — otherwise it "
            + "would fold as a signed-in user and inherit the Public grants");

        var read = () => ExactRead(VirtualGuest(), "AnonOpen/Page", ct);
        await read.Should().ThrowAsync<UnauthorizedAccessException>(
            "the cached exact-node read gates on the same fold, with the caller's own captured id");
    }

    /// <summary>
    /// <c>Select</c> and <c>Autocomplete</c> carry no request viewer, so the ambient context decides:
    /// a virtual one, or one that names nobody, is logged out and gets nothing.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbientLoggedOut_SelectAndAutocomplete_AnswerNothing(bool isVirtual)
    {
        var ct = TestContext.Current.CancellationToken;
        ActAs(isVirtual ? VirtualGuest() : new AccessContext { ObjectId = "" });

        (await MeshQuery.Autocomplete("AnonOpen", "Pa")
                .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct))
            .Should().BeEmpty("a logged-out ambient caller is refused at the read boundary");
        (await MeshQuery.Select<string>("AnonOpen/Page", "name")
                .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct))
            .Should().BeNull();
        (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:AnonOpen/Page"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct))
            .Items.Should().BeEmpty("a query that names no viewer takes the logged-out ambient one");
    }

    /// <summary>
    /// The RLS node validator refuses a logged-out caller AHEAD of the hub and per-type rule chain
    /// (either of which may answer without reaching the fold) — in the caller's language.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RlsValidator_RefusesALoggedOutCaller_Localized()
    {
        var ct = TestContext.Current.CancellationToken;

        var named = await RlsRead(new AccessContext { ObjectId = WellKnownUsers.Anonymous, Locale = "en" }, "AnonOpen/Page", ct);
        named.IsValid.Should().BeFalse();
        named.ErrorMessage.Should().Contain("does not allow anonymous access");

        var virtualDe = await RlsRead(VirtualGuest("de"), "AnonOpen/Page", ct);
        virtualDe.IsValid.Should().BeFalse("a virtual context is logged out whatever id it carries");
        virtualDe.ErrorMessage.Should().Contain("anonymen Zugriff", "the refusal follows the viewer's language");

        (await RlsRead(new AccessContext { ObjectId = SignedIn }, "AnonOpen/Page", ct)).IsValid
            .Should().BeTrue("the control: a signed-in caller still reads through the Public grant");
    }

    /// <summary>A link preview is a disclosure to a logged-out caller — none on a closed instance.</summary>
    [Fact(Timeout = 60_000)]
    public async Task PublicPreview_IsOff()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Mesh.GetPublicPreview("AnonOpen").FirstAsync().Timeout(TestTimeouts.Quick).Await(ct))
            .Should().BeFalse();
    }
}

/// <summary>
/// The switch NOT stated: zero behaviour change. Every path the on-class refuses is readable by the
/// anonymous subject here, through the grant it carries — the control for every "on" reading.
/// </summary>
public class DenyAnonymousSwitchOffTest(ITestOutputHelper output) : DenyAnonymousSwitchTestBase(output, denyAnonymous: false)
{
    /// <summary>The grants the on-class overrides really do open these paths to the anonymous subject.</summary>
    [Theory(Timeout = 60_000)]
    [InlineData("AnonOpen/Page")]
    [InlineData("PolicyOpen/Page")]
    public async Task AnonymousRead_FollowsTheGrants(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        (await CanRead(path, WellKnownUsers.Anonymous, ct)).Should().BeTrue(
            "with the switch off an Anonymous Viewer grant / a PublicRead policy grants the anonymous "
            + "subject Read, exactly as before the switch existed");
    }

    /// <summary>Public is the signed-in baseline, never applied to the anonymous subject — switch or not.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AnonymousRead_NeverInheritsPublic()
    {
        var ct = TestContext.Current.CancellationToken;
        (await CanRead("SignedInOnly/Page", WellKnownUsers.Anonymous, ct)).Should().BeFalse();
    }

    /// <summary>
    /// The controls for the virtual-caller and validator readings: with the switch off a virtual
    /// visitor folds exactly as before (its guest id inherits the Public grant), reads the node by
    /// exact path, and passes the RLS validator — so the on-class refusals are the switch's doing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task VirtualCaller_FoldsAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        (await EffectiveUnder(VirtualGuest(), "AnonOpen/Page", ct)).HasFlag(Permission.Read).Should().BeTrue();
        (await ExactRead(VirtualGuest(), "AnonOpen/Page", ct)).Path.Should().Be("AnonOpen/Page");
        (await RlsRead(new AccessContext { ObjectId = WellKnownUsers.Anonymous }, "AnonOpen/Page", ct)).IsValid
            .Should().BeTrue("the Anonymous Viewer grant opens the node when the switch is off");
    }

    /// <summary>The anonymous gate grants what the grant opens.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AnonymousGate_Grants()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await AnonymousGate.Evaluate(Mesh, "AnonOpen/Page")
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct);
        outcome.IsGranted.Should().BeTrue();
    }
}

/// <summary>The switch's parsing: absent, empty and unparseable are all OFF.</summary>
public class AnonymousAccessParsingTest
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void IsDenied_OnlyForAStatedTrue(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            ImmutableDictionary<string, string?>.Empty.Add(AnonymousAccess.DenyAnonymousConfigKey, value)).Build();
        AnonymousAccess.IsDenied(configuration).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(WellKnownUsers.Anonymous, true)]
    [InlineData("anonymous", true)]
    [InlineData(WellKnownUsers.Public, false)]
    [InlineData(WellKnownUsers.System, false)]
    [InlineData("alice", false)]
    public void IsAnonymousSubject(string? userId, bool expected)
        => AnonymousAccess.IsAnonymousSubject(userId).Should().Be(expected);

    [Fact]
    public void EnvironmentForm_BindsToTheSameKey()
    {
        // Access__DenyAnonymous is how AKS and Container Apps deliver it; .NET maps "__" to ":".
        const string env = "Access__DenyAnonymous";
        env.Replace("__", ":").Should().Be(AnonymousAccess.DenyAnonymousConfigKey);
    }
}
