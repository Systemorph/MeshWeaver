using System.Reactive.Linq;
using MeshWeaver.Fixture;
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
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AnonymousAccess.DenyAnonymousConfigKey] = DenyAnonymous ? "true" : null,
                }).Build()));

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
            new Dictionary<string, string?> { [AnonymousAccess.DenyAnonymousConfigKey] = value }).Build();
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
