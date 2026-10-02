using System.Reactive.Linq;
using System.Security.Claims;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Service identities (Doc/Architecture/ServiceIdentities) against a REAL mesh: a non-person
/// principal a global admin creates, grants and issues tokens for, whose tokens authenticate as the
/// SERVICE — never as a person — and which never holds platform administration.
///
/// <para>🚨 Built on <c>ConfigureMeshBase</c>, NOT <c>base.ConfigureMesh</c>: the latter grants
/// Public the Admin role everywhere, under which "a service token can create a node only where
/// granted" would pass vacuously. The only standing grant here is the harness admin's (Roland's) own
/// root grant, which is the identity every setup step runs under.</para>
/// </summary>
public class ServiceIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string GrantedScope = TestPartition + "/Granted";
    private const string OtherScope = TestPartition + "/Other";

    /// <summary>A service whose Admin-partition grant is seeded STATICALLY — the one road around the
    /// write guard — so the read-side refusal can be measured on its own.</summary>
    private const string SeededAdminService = "svc-seeded-admin";

    /// <summary>A PERSON with the same static Admin-partition grant: the positive control.</summary>
    private const string SeededAdminPerson = "seeded-admin-person";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("Granted", TestPartition) { Name = "Granted", NodeType = "Markdown" },
                new MeshNode("Other", TestPartition) { Name = "Other", NodeType = "Markdown" },
                AdminPartitionGrant(SeededAdminService),
                AdminPartitionGrant(SeededAdminPerson));

    private static MeshNode AdminPartitionGrant(string subject) => new($"{subject}_Access", "Admin/_Access")
    {
        NodeType = AccessAssignmentNodeType.NodeType,
        Name = $"{subject} — Admin",
        MainNode = "Admin",
        Content = new AccessAssignment
        {
            AccessObject = subject,
            DisplayName = subject,
            Roles = [new RoleAssignment { Role = "Admin" }],
        },
    };

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private ApiTokenService Tokens() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
        Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    /// <summary>Both contexts — see NonAdminUpdateStatusTest.Become for why.</summary>
    private void Become(AccessContext user)
    {
        Access.SetContext(user);
        Access.SetHostIdentity(user);
    }

    private void BecomeAdmin() => Become(TestUsers.Admin);

    /// <summary>The error message of a stream that should fail, or <c>null</c> when it emitted.</summary>
    private async Task<string?> Refusal<T>(IObservable<T> operation)
    {
        var message = await operation
            .Take(1)
            .Select(_ => (string?)null)
            .Catch<string?, Exception>(ex => Observable.Return<string?>(ex.Message))
            .Should().Emit();
        Output.WriteLine($"refusal: {message ?? "(none — the operation succeeded)"}");
        return message;
    }

    /// <summary>Creates an identity and issues it a token, as the admin. Returns the raw token.</summary>
    private async Task<(string ObjectId, TokenCreationResult Token)> ServiceWithToken(
        ApiTokenService tokens, string name)
    {
        BecomeAdmin();
        var identity = await ServiceIdentities.Create(Mesh, name, "test service").Should().Emit();
        identity.Path.Should().Be(ServiceIdentity.PathFor(identity.Id));
        var token = await tokens.CreateServiceToken(identity.Id, "ci").Should().Emit();
        return (identity.Id, token);
    }

    /// <summary>
    /// The acceptance test: a service token authenticates as the SERVICE, and that principal can
    /// create a node exactly where it was granted — not one scope over — with audit naming it.
    /// </summary>
    [Fact]
    public async Task AServiceToken_CreatesANodeOnlyWhereGranted_AndAuditShowsTheService()
    {
        var tokens = Tokens();
        var (serviceId, token) = await ServiceWithToken(tokens, "Name Check");
        serviceId.Should().Be("svc-name-check");

        await ServiceIdentities.Grant(Mesh, serviceId, GrantedScope, "Editor").Should().Emit();

        // Both validation paths agree: the auth handler's (storage-direct) and the middleware's (hub).
        var validated = await tokens.Validate(token.RawToken).Should().Emit();
        validated.Status.Should().Be(TokenValidationStatus.Valid);
        validated.Token.Should().NotBeNull();
        validated.Token?.UserId.Should().Be(serviceId,
            "a service token authenticates as the service principal, never as the admin who issued it");
        validated.Token?.ServiceIdentityPath.Should().Be(ServiceIdentity.PathFor(serviceId));

        var viaHub = await UserContextMiddleware.ValidateTokenViaHub(token.RawToken, Mesh).Should().Emit();
        viaHub.Should().NotBeNull();
        viaHub?.Success.Should().BeTrue();
        viaHub?.IsService.Should().BeTrue();
        viaHub?.UserId.Should().Be(serviceId);

        Become(new AccessContext
        {
            ObjectId = viaHub?.UserId ?? "",
            Name = viaHub?.UserName ?? "",
            IsApiToken = true,
            IsService = true,
        });

        var created = await NodeFactory.CreateNode(new MeshNode("by-service", GrantedScope)
        {
            Name = "Written by the service",
            NodeType = "Markdown",
        }).Should().Emit("the service holds Editor at this scope");
        created.CreatedBy.Should().Be(serviceId, "audit must show the service id, not a person");

        var refused = await Refusal(NodeFactory.CreateNode(new MeshNode("by-service", OtherScope)
        {
            Name = "Must not be written",
            NodeType = "Markdown",
        }));
        refused.Should().NotBeNull("the service was granted nothing at the sibling scope");
        refused.Should().Contain("Access denied", "it is the permission check that refuses, not anything else");
    }

    [Fact]
    public async Task RevokingTheIdentity_StopsEveryTokenItHolds()
    {
        var tokens = Tokens();
        var (serviceId, token) = await ServiceWithToken(tokens, "Revoked Bot");
        (await tokens.Validate(token.RawToken).Should().Emit()).Status.Should().Be(TokenValidationStatus.Valid);

        await ServiceIdentities.Revoke(Mesh, serviceId).Should().Emit();

        var after = await tokens.Validate(token.RawToken).Should().Emit();
        after.Status.Should().Be(TokenValidationStatus.Invalid,
            "validation reads the identity on every use — revoking it revokes its tokens at once");
        var viaHub = await UserContextMiddleware.ValidateTokenViaHub(token.RawToken, Mesh).Should().Emit();
        viaHub?.Success.Should().BeFalse("the middleware path must agree with the auth handler's");

        (await Refusal(tokens.CreateServiceToken(serviceId, "after revoke"))).Should().NotBeNull(
            "a revoked identity is issued nothing");
    }

    /// <summary>
    /// An admin may DELETE a record instead of revoking it. Both validation paths must still answer
    /// a definitive negative — the hub path used to route to an absent node, which is a NotFound
    /// (or a stall), not a verdict.
    /// </summary>
    [Fact]
    public async Task DeletingTheIdentityRecord_RefusesItsTokens_OnBothPaths()
    {
        var tokens = Tokens();
        var (serviceId, token) = await ServiceWithToken(tokens, "Deleted Bot");
        (await tokens.Validate(token.RawToken).Should().Emit()).Status.Should().Be(TokenValidationStatus.Valid);

        BecomeAdmin();
        await NodeFactory.DeleteNode(ServiceIdentity.PathFor(serviceId)).Should().Emit();

        (await tokens.Validate(token.RawToken).Should().Emit()).Status.Should().Be(TokenValidationStatus.Invalid,
            "an absent identity record authenticates nobody");
        var viaHub = await UserContextMiddleware.ValidateTokenViaHub(token.RawToken, Mesh).Should().Emit();
        viaHub?.Success.Should().BeFalse();
        viaHub?.IsUnavailable.Should().BeFalse("an absent record is a verdict, not an outage");
    }

    [Fact]
    public async Task RevokingAnUnknownIdentity_IsRefusedByName()
    {
        BecomeAdmin();
        var refusal = await Refusal(ServiceIdentities.Revoke(Mesh, "svc-typo"));
        refusal.Should().Contain("No service identity 'svc-typo'",
            "a typo'd or deleted id is refused, never reported as revoked");
    }

    [Fact]
    public async Task RotatingAToken_IssuesANewOne_AndRevokesTheOld()
    {
        var tokens = Tokens();
        var (serviceId, token) = await ServiceWithToken(tokens, "Rotating Bot");

        var rotated = await ServiceIdentities
            .Rotate(tokens, serviceId, token.Node.Path, "ci", null).Should().Emit();

        (await tokens.Validate(rotated.RawToken).Should().Emit()).Status.Should().Be(TokenValidationStatus.Valid);
        (await tokens.Validate(token.RawToken).Should().Emit()).Status.Should().Be(TokenValidationStatus.Invalid,
            "the old token is revoked once the new one exists");
    }

    [Fact]
    public async Task UsingAServiceToken_StampsLastUsed()
    {
        var tokens = Tokens();
        var (_, token) = await ServiceWithToken(tokens, "Stamped Bot");

        (await tokens.Validate(token.RawToken).Should().Emit()).Status.Should().Be(TokenValidationStatus.Valid);

        BecomeAdmin();
        await Mesh.GetWorkspace().GetMeshNodeStream(token.Node.Path)
            .Select(n => n?.ContentAs<ApiToken>(Mesh.JsonSerializerOptions)?.LastUsedAt)
            .Should().Within(TimeSpan.FromSeconds(20))
            .Match(lastUsed => lastUsed is not null,
                "the last-used stamp lands on the token row beneath the service record");
    }

    /// <summary>The service surface names only services; the person surface never mints for one.</summary>
    [Fact]
    public async Task IssuingForAPerson_OrForAnUnknownService_IsRefused()
    {
        var tokens = Tokens();
        BecomeAdmin();

        (await Refusal(tokens.CreateServiceToken(TestUsers.Admin.ObjectId, "impersonation")))
            .Should().NotBeNull("the service surface can never be pointed at a person");
        (await Refusal(tokens.CreateServiceToken("svc-never-created", "ghost")))
            .Should().NotBeNull("no record, no token");
        (await Refusal(tokens.CreateToken("svc-never-created", "x", "", "via the person path")))
            .Should().NotBeNull("a person's token surface never mints for a service principal");
    }

    [Fact]
    public async Task AServicePrincipal_NeverHoldsGlobalAdmin()
    {
        (await Mesh.IsGlobalAdmin(SeededAdminPerson).Should().Emit()).Should().BeTrue(
            "positive control: the same static Admin-partition grant makes a PERSON a global admin");
        (await Mesh.IsGlobalAdmin(SeededAdminService).Should().Emit()).Should().BeFalse(
            "a service principal never administers the platform, whatever a grant says");

        // The CURRENT-caller form every admin gate calls (AdminOnlyTab, AdminMenuGate) — it resolves
        // the ambient caller and delegates to the explicit overload, so the refusal holds there too.
        Become(new AccessContext
        {
            ObjectId = SeededAdminService, Name = SeededAdminService, IsApiToken = true, IsService = true,
        });
        (await Mesh.IsGlobalAdmin().Should().Emit()).Should().BeFalse(
            "the current-caller form must refuse a service exactly as the explicit one does");
        Become(new AccessContext { ObjectId = SeededAdminPerson, Name = SeededAdminPerson });
        (await Mesh.IsGlobalAdmin().Should().Emit()).Should().BeTrue(
            "positive control for the current-caller form");

        var tokens = Tokens();
        var (serviceId, _) = await ServiceWithToken(tokens, "Would Be Admin");
        BecomeAdmin();
        var refused = await Refusal(NodeFactory.CreateNode(AdminPartitionGrant(serviceId)));
        refused.Should().NotBeNull("no Admin-partition grant may name a service principal");
        refused.Should().Contain("service principal", "the refusal is the guard's, not an unrelated failure");

        // The seeded service DOES hold Admin rights on the Admin partition (the static grant goes
        // around the write guard), so RLS would let it write there — the guard is what refuses.
        Become(new AccessContext
        {
            ObjectId = SeededAdminService, Name = SeededAdminService, IsApiToken = true, IsService = true,
        });
        var selfWrite = await Refusal(ServiceIdentities.Create(Mesh, "Minted By A Service", null));
        selfWrite.Should().NotBeNull("a service principal writes nothing in the Admin partition");
        selfWrite.Should().Contain("may not write in the Admin partition",
            "the refusal is the service guard's, even though RLS alone would have allowed the write");
    }

    [Fact]
    public void OnlyTheTokenSchemeCanAssertTheServiceKind()
    {
        Claim[] claims =
        [
            new("preferred_username", "svc-name-check"),
            new(ServiceIdentity.PrincipalKindClaim, ServiceIdentity.ServicePrincipalKind),
        ];
        UserContextMiddleware.IsServiceTokenIdentity(
                new ClaimsPrincipal(new ClaimsIdentity(claims, ServiceIdentity.TokenAuthenticationType)))
            .Should().BeTrue();
        UserContextMiddleware.IsServiceTokenIdentity(
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")))
            .Should().BeFalse("a cookie or an external provider cannot make a session a service");
    }

    [Theory]
    [InlineData("Name Check", "svc-name-check")]
    [InlineData("svc-namecheck", "svc-namecheck")]
    [InlineData("  CI / Bot  ", "svc-ci-bot")]
    [InlineData("???", null)]
    public void ObjectIdsAreSlugsWithTheServicePrefix(string name, string? expected)
        => ServiceIdentity.ObjectIdFor(name).Should().Be(expected);
}
