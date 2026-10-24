using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Hosting.SelfUpdate;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The build principal end to end (#2483): a GitHub Actions OIDC token, verified against a JWKS this
/// test controls, resolved to a <c>BuildPrincipal</c> node in the Admin partition, and finally
/// decided at the prebuilt-publication routes.
///
/// <para>🚨 <b>The refusal matrix is the point.</b> Every negative below starts from a token that
/// WOULD be accepted and moves exactly one thing — the repository, the audience, the expiry, the
/// node, the scope — so a passing assertion can only mean that one thing was checked. A verifier
/// that checked the signature and stopped would authenticate every workflow run on GitHub.</para>
///
/// <para>🚨 <b>And the third state.</b> An unreadable JWKS answers <c>503</c>, never <c>401</c>:
/// "I could not find out" is not a denial and certainly not an admission (core #2901). The test
/// asserts the STATUS, because a build told "your identity is unknown" goes looking for a
/// credential that was never the problem.</para>
/// </summary>
public class BuildPrincipalAuthenticationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Audience = "https://registry.build-principal.test";
    private const string Repository = "Systemorph/MeshWeaver.SocialMedia";
    private const string DeclaredRepository = "Systemorph/MeshWeaver.Declared";
    private const string Identity = "s0123456789abcdef0123456789abcdef";
    private const string Source = "plugins";

    private readonly GitHubTokenFactory tokens = new();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        tokens.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>What the JWKS leaf answers next. A test swaps it to stage a rotation or an outage.
    /// Instance state, so it dies with the test class — never a static.</summary>
    private volatile Func<string> jwks = () => "";

    // ── the lagging-mirror seam ───────────────────────────────────────────────────────────────
    //
    // While armed, every DataChangedEvent the principal's OWNER posts for {heldPath} — the commit
    // echo that advances this process's live mirror — is held at the owner's outbound pipeline
    // instead of sent. The owner's ACK (PatchDataResponse) is untouched, so the revoke completes
    // exactly as it does when the echo is merely slow (a loaded runner, a busy cache hub, another
    // replica). This is the ordering the full-run failure hit by chance, held on purpose. Released by
    // the test, which re-delivers the held frames in order. Instance fields: per-test lifetime.
    private volatile string? heldPath;
    private ImmutableList<(IMessageHub Owner, IMessageDelivery Delivery)> heldEcho = [];
    private readonly AsyncSubject<Unit> echoHeld = new();

    private void ReleaseEcho()
    {
        heldPath = null;
        foreach (var (owner, delivery) in Interlocked.Exchange(ref heldEcho, []))
            owner.DeliverMessage(delivery);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(config => config.AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (heldPath is not { } held
                    || d.Message is not DataChangedEvent
                    || !string.Equals(p.Hub.Address.Path, held, StringComparison.Ordinal))
                    return next.Invoke(d);
                ImmutableInterlocked.Update(ref heldEcho, list => list.Add((p.Hub, d)));
                echoHeld.OnNext(Unit.Default);
                echoHeld.OnCompleted();
                return d.Ignored();
            })))
            .AddPluginCatalog()
            .AddUpdatePolicyType()
            .ConfigureServices(services => services
                // The audience is the one deployment knob the build-principal leg has, and an
                // unconfigured one refuses everything — so a test that wants the leg live has to
                // name it, exactly as an operator does.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [BuildPrincipalConfiguration.AudienceConfigKey] = Audience,
                        // One repository this deployment DECLARES instead of holding a node for —
                        // in the key shape a portal's environment produces. The ids are the ones
                        // GitHubTokenFactory mints.
                        [$"{DeclaredBuildPrincipals.ConfigSection}:0:Repository"] = DeclaredRepository,
                        [$"{DeclaredBuildPrincipals.ConfigSection}:0:RepositoryId"] = "123456789",
                        [$"{DeclaredBuildPrincipals.ConfigSection}:0:RepositoryOwnerId"] = "9999",
                        [$"{DeclaredBuildPrincipals.ConfigSection}:0:Events:workflow_run:0"] = BuildVerbs.Verify,
                        [$"{DeclaredBuildPrincipals.ConfigSection}:0:EventRefs:workflow_run:0"] = "refs/heads/main",
                        [$"{DeclaredBuildPrincipals.ConfigSection}:0:Scopes:0"] = "verify:combo",
                    })
                    .Build())
                // 🚨 TRANSIENT here, singleton in production. The service caches a key set for an
                // hour on purpose; a per-request instance lets each fact stage its own JWKS (a
                // rotation, an outage) without a Clear() for test isolation, which would be the tell
                // of an unfixed root cause. The cache and refresh-floor rules are pinned separately
                // below, against one instance driven directly.
                .AddTransient(sp => new GitHubOidcKeyService(
                    sp.GetRequiredService<IMessageHub>(),
                    sp.GetRequiredService<ILogger<GitHubOidcKeyService>>())
                {
                    FetchOverride = _ => Task.FromResult(jwks()),
                }));

    private InstanceRegistryAuthenticator Authenticator() => new(
        Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());

    private Task<InstanceAuthResult> Authenticate(string token, CancellationToken cancellationToken = default) =>
        Authenticator().AuthenticateOutcome($"Bearer {token}")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(cancellationToken);

    /// <summary>Writes a build principal into the Admin partition — the create a global admin
    /// makes. As System, because the Admin partition is exactly what an ordinary identity cannot
    /// write.</summary>
    private Task<MeshNode> Grant(BuildPrincipal principal, string? repository = null,
        CancellationToken cancellationToken = default)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = BuildPrincipal.NodeId(repository ?? principal.Repository);
        var node = new MeshNode(id, BuildPrincipal.Namespace)
        {
            Name = $"Build principal: {principal.Repository}",
            NodeType = MeshWeaverInstanceNodeType.BuildPrincipalNodeType,
            State = MeshNodeState.Active,
            Content = principal,
        };
        return access.RunAsSystem(() => meshService.CreateOrUpdateNode(node))
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
    }

    private static BuildPrincipal Principal(string repository = Repository) => new()
    {
        Repository = repository,
        Scopes = [$"{BuildVerbs.Fetch}:{Source}", $"{BuildVerbs.Publish}:socialmedia"],
        Events = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["push"] = [BuildVerbs.Fetch, BuildVerbs.Publish],
            ["pull_request"] = [BuildVerbs.Fetch],
        },
        IssuedBy = "admin",
        IssuedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    // ── the authenticator ────────────────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task AVerifiedToken_ResolvesToItsPrincipal_AndOnlyWhenOneExists()
    {
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.NoPrincipalYet";

        // 1. No node for the repository → a DEFINITIVE negative. A verified signature is not an
        //    authorization: this is the "no node = 401" the design names.
        var stranger = await Authenticate(tokens.Mint(Audience, repository: repository), TestContext.Current.CancellationToken);
        Assert.False(stranger.IsUnavailable);
        Assert.Null(stranger.Instance);
        Assert.Null(stranger.Build);

        // 2. Same token, once a global admin has written the rule → authenticated.
        await Grant(Principal(repository));
        var trusted = await Authenticate(tokens.Mint(Audience, repository: repository));
        Assert.False(trusted.IsUnavailable);
        Assert.NotNull(trusted.Build);
        Assert.Equal(repository, trusted.Build!.Repository);
        // 🚨 …and it is NOT an instance. Every surface that needs an installation keeps refusing it.
        Assert.Null(trusted.Instance);
    }

    [Fact(Timeout = 240_000)]
    public async Task ANodeThatNamesAnotherRepository_DoesNotAuthenticateTheToken()
    {
        // The path is a routing hint; the RECORD is the authority. A node hand-edited (or migrated)
        // so its path and its declared repository disagree must authenticate nobody.
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.PathDrift";
        await Grant(Principal("Systemorph/SomethingElse"), repository, TestContext.Current.CancellationToken);

        var result = await Authenticate(tokens.Mint(Audience, repository: repository));

        Assert.False(result.IsUnavailable);
        Assert.Null(result.Build);
    }

    [Fact(Timeout = 240_000)]
    public async Task ARevokedPrincipal_StopsAuthenticatingAtOnce()
    {
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.Revoked";
        await Grant(Principal(repository), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull((await Authenticate(tokens.Mint(Audience, repository: repository))).Build);

        // The control-plane verb — no watcher stands between writing it and the refusal.
        await Grant(Principal(repository) with { RequestedAction = BuildPrincipalActions.Revoke });

        var result = await Authenticate(tokens.Mint(Audience, repository: repository));
        Assert.False(result.IsUnavailable);
        Assert.Null(result.Build);
    }

    [Fact(Timeout = 240_000)]
    public async Task ARevokedPrincipal_IsRefused_WhileThisProcesssMirrorStillCarriesTheGrant()
    {
        // 🚨 The deterministic form of the full-run failure (ARevokedPrincipal_StopsAuthenticatingAtOnce
        // authenticated a revoked principal once). The revoke is ACKNOWLEDGED by the owner — it has
        // committed — while the echo that would advance this process's mirror is held. Any read of the
        // principal that answers from that mirror authenticates a revoked build for as long as the
        // echo is late; on another replica that is as long as the cross-process feed is late. The
        // verdict must come from the owner, so the refusal holds from the moment the revoke commits.
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.RevokedEchoHeld";
        var token = tokens.Mint(Audience, repository: repository);
        await Grant(Principal(repository), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull((await Authenticate(token, TestContext.Current.CancellationToken)).Build);

        // This process's mirror, hydrated with the ACTIVE principal BEFORE the echo is held — the
        // state any reader in this process had when the full run failed. Held open for the whole
        // test, and asserted on again at the verdict, so the window is provably open when it is read.
        var path = BuildPrincipal.PathFor(repository);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var mirror = new ReplaySubject<BuildPrincipal?>(1);
        using var mirrorSubscription = access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)
                .Select(node => node.ContentAs<BuildPrincipal>(Mesh.JsonSerializerOptions)))
            .Subscribe(mirror);
        await mirror.Where(p => p is not null && p.IsActive(DateTimeOffset.UtcNow)).Should()
            .Within(TimeSpan.FromSeconds(60)).Emit("this process's mirror carries the active principal");

        heldPath = path;
        try
        {
            await Grant(Principal(repository) with { RequestedAction = BuildPrincipalActions.Revoke },
                cancellationToken: TestContext.Current.CancellationToken);
            // Not vacuous: the owner committed the revoke AND posted its echo, which the seam holds —
            // so this process's mirror provably still carries the active principal right now.
            await echoHeld.Should().Within(TimeSpan.FromSeconds(60)).Emit(
                "the owner committed the revoke and posted its echo — which the seam is holding");

            var result = await Authenticate(token, TestContext.Current.CancellationToken);
            Assert.False(result.IsUnavailable, "a committed revoke is a VERDICT, never an outage");
            Assert.Null(result.Build);
            // …while the mirror still says ACTIVE: the refusal did not come from it.
            await mirror.Take(1).Where(p => p is not null && p.IsActive(DateTimeOffset.UtcNow)).Should()
                .Within(TimeSpan.FromSeconds(10)).Emit("the held echo keeps this process's mirror on the pre-revoke record");
        }
        finally
        {
            ReleaseEcho();
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task AWrongAudienceOrAnExpiredToken_IsRefused_NotUnavailable()
    {
        jwks = () => tokens.Jwks();
        await Grant(Principal(), cancellationToken: TestContext.Current.CancellationToken);

        foreach (var token in new[]
                 {
                     tokens.Mint("api://AzureADTokenExchange"),
                     tokens.Mint(Audience, issuedAt: DateTimeOffset.UtcNow.AddHours(-2)),
                     tokens.Mint(Audience, issuer: "https://evil.example"),
                 })
        {
            var result = await Authenticate(token);
            Assert.False(result.IsUnavailable, "a bad token is a VERDICT, not an outage");
            Assert.Null(result.Build);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task AnUnreachableKeySet_IsUNDETERMINED_NeverUnknownToken()
    {
        // 🚨 Fail closed AND distinguishable. The build is told "retry", not "your identity is
        // unknown" — the latter sends an operator hunting a credential that was never the problem
        // (the #2695 shape, on the leg that did not exist yet).
        jwks = () => throw new HttpRequestException("JWKS unreachable");
        await Grant(Principal(), cancellationToken: TestContext.Current.CancellationToken);

        var result = await Authenticate(tokens.Mint(Audience));

        Assert.True(result.IsUnavailable);
        Assert.Null(result.Build);
        Assert.Null(result.Instance);
    }

    [Fact(Timeout = 240_000)]
    public async Task AKeySetThatPublishesNothingUsable_IsUNDETERMINED_NotAnAdmission()
    {
        // An empty key set would refuse everything, which is correct — but it must not be REMEMBERED
        // for an hour as if it were a valid answer, so the read throws and stays retryable.
        jwks = () => """{"keys":[]}""";
        await Grant(Principal(), cancellationToken: TestContext.Current.CancellationToken);

        var result = await Authenticate(tokens.Mint(Audience));

        Assert.True(result.IsUnavailable);
        Assert.Null(result.Build);
    }

    // ── the key service's own cache ──────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task TheKeySetIsReadOnce_AFaultIsNeverCached_AndTheRefreshFloorBoundsRe_Reads()
    {
        var reads = 0;
        var document = tokens.Jwks();
        var fail = true;
        var service = new GitHubOidcKeyService(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<GitHubOidcKeyService>>())
        {
            FetchOverride = _ =>
            {
                Interlocked.Increment(ref reads);
                return fail
                    ? Task.FromException<string>(new HttpRequestException("down"))
                    : Task.FromResult(document);
            },
        };
        var now = DateTimeOffset.UtcNow;

        // A failure propagates AND is not cached — the next caller starts a genuinely new attempt
        // rather than replaying a latched OnError (the ReplaySubject trap, #1369).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken));
        Assert.Equal(2, reads);

        // 🚨 A ROTATION ARRIVING WHILE THE CACHE IS EMPTY. The fault above evicted the promise, so
        // this refresh has nothing to replace — and must still start a real read rather than letting
        // a null observable escape into the authenticator's SelectMany, where the failure would
        // surface far from its cause (Copilot review, PR #2988).
        fail = false;
        var afterFault = await service
            .Refresh(new GitHubSigningKeys(
                new Dictionary<string, GitHubSigningKey>(), now - TimeSpan.FromHours(1)), now)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Single(afterFault.ByKeyId);
        Assert.Equal(3, reads);

        var first = await service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Single(first.ByKeyId);
        // …and it was SHARED with the refresh above rather than starting a round trip of its own.
        Assert.Equal(3, reads);

        // A success is shared: a second caller inside the window costs no round trip.
        await service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Equal(3, reads);

        // 🚨 The refresh floor. An unknown kid may force ONE early re-read; inside the floor it
        // returns the set unchanged, so a caller inventing key ids cannot amplify into a fetch per
        // request.
        var suppressed = await service.Refresh(first, now).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Equal(3, reads);
        Assert.Equal(first.FetchedAt, suppressed.FetchedAt);

        // Past the floor it really does re-read — a rotation is recoverable without a restart.
        await service.Refresh(first, now + GitHubOidcKeyService.MinimumRefreshInterval + TimeSpan.FromSeconds(1))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Equal(4, reads);

        // And a stale set is re-read on the ordinary path too.
        await service.Keys(now + GitHubOidcKeyService.CacheDuration + TimeSpan.FromMinutes(1))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Equal(5, reads);
    }

    // ── the endpoint ─────────────────────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task ThePrebuiltRoutesServeATrustedBuild_AndRefuseEveryoneElse()
    {
        jwks = () => tokens.Jwks();
        var root = Path.Combine(Path.GetTempPath(), "mw-buildprincipal-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, Identity, Source);
        Directory.CreateDirectory(dir);
        try
        {
            WriteBundle(Path.Combine(dir, "Store.zip"));
            File.WriteAllText(
                Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");

            await Grant(Principal(), cancellationToken: TestContext.Current.CancellationToken);
            await using var app = await StartHost(root);
            var route = $"/api/plugins/bundles/prebuilt/{Identity}/{Source}";

            // 1. the designed case — a push from the trusted repo, holding fetch:plugins
            var ok = await Get(app, route, tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("Store.zip", await ok.Content.ReadAsStringAsync());
            var bytes = await Get(app, route + "/Store.zip", tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);

            // 2. 🚨 a valid signature from ANOTHER repository — 401, because no rule names it
            var other = await Get(app, route, tokens.Mint(Audience, repository: "Systemorph/Evil"));
            Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

            // 3. the right repository on an event its rule does not carry → 403, never the bytes
            var wrongEvent = await Get(app, route, tokens.Mint(Audience, eventName: "workflow_dispatch"));
            Assert.Equal(HttpStatusCode.Forbidden, wrongEvent.StatusCode);

            // 4. a source the principal holds no fetch scope for → 403
            var otherSource = await Get(
                app, $"/api/plugins/bundles/prebuilt/{Identity}/education", tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.Forbidden, otherSource.StatusCode);

            // 5. 🚨 a build principal is NOT an installation: every other bundle route still 401s,
            //    because it decides per package against a grant and a plan a build does not have.
            var index = await Get(app, "/api/plugins/bundles/index.json", tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.Unauthorized, index.StatusCode);

            // 6. an unreadable key set is 503 + Retry-After, never the 401 a bad token gets
            jwks = () => throw new HttpRequestException("JWKS unreachable");
            var unavailable = await Get(app, route, tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task ComboVerification_BuildCanReadAndRecord_WithoutChangingDisabledUpdates()
    {
        jwks = () => tokens.Jwks();
        var principal = VerificationPrincipal();
        await Grant(principal);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateOrUpdateNode(new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
            {
                Name = "Update policy", NodeType = UpdatePolicyNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.None,
                    LatestAvailableTag = "3.0.0-ci.123" },
            })).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await using var app = await StartHost(Path.GetTempPath());
        var token = tokens.Mint(Audience, eventName: "workflow_run");
        using var combo = await Get(app, ReleaseGateEndpoints.ComboRoute, token);
        Assert.Equal(HttpStatusCode.OK, combo.StatusCode);
        Assert.Contains("isComplete", await combo.Content.ReadAsStringAsync());

        var verdict = new ComboVerification
        {
            CandidateTag = "3.0.0-ci.123", VerifiedAt = DateTimeOffset.UtcNow,
            Verdict = ComboVerdictKind.NotVerifiable, Caveats = ["Test could not execute the candidate"],
        };
        using var recorded = await PostVerdict(app, token,
            JsonSerializer.Serialize(verdict, InstanceComboAssembler.Json));
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        Assert.Contains("\"recorded\":true", await recorded.Content.ReadAsStringAsync());
        var policy = await access.RunAsSystem(() => Mesh.GetWorkspace()
                .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(n => UpdatePolicyNodeType.Parse(n, Mesh.JsonSerializerOptions)))
            .Where(c => c.VerificationFor(verdict.CandidateTag)?.VerifiedAt == verdict.VerifiedAt)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePolicyKind.None, policy.Policy);
        Assert.Equal("3.0.0-ci.123", policy.LatestAvailableTag);
        Assert.Equal(ComboVerdictKind.NotVerifiable, policy.VerificationFor(verdict.CandidateTag)!.Verdict);

        // Revocation is observed on the very next request, including the write route.
        await Grant(principal with { RequestedAction = BuildPrincipalActions.Revoke });
        using var revoked = await PostVerdict(app, token, "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory(Timeout = 240_000)]
    [InlineData("scope")]
    [InlineData("event")]
    [InlineData("ref")]
    [InlineData("repository")]
    [InlineData("repositoryId")]
    [InlineData("ownerId")]
    [InlineData("audience")]
    [InlineData("expired")]
    public async Task ComboVerification_RefusesAChangedTrustInput(string changed)
    {
        jwks = () => tokens.Jwks();
        var principal = VerificationPrincipal();
        if (changed == "scope") principal = principal with { Scopes = ["fetch:plugins"] };
        if (changed == "ownerId") principal = principal with { RepositoryOwnerId = "wrong-owner" };
        await Grant(principal, cancellationToken: TestContext.Current.CancellationToken);
        var token = tokens.Mint(changed == "audience" ? "https://another.portal.test" : Audience,
            repository: changed == "repository" ? "Systemorph/AnotherRepo" : Repository,
            repositoryId: changed == "repositoryId" ? "wrong-id" : "123456789",
            eventName: changed == "event" ? "pull_request" : "workflow_dispatch",
            gitRef: changed == "ref" ? "refs/heads/feature" : "refs/heads/main",
            issuedAt: changed == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : null);
        await using var app = await StartHost(Path.GetTempPath());
        foreach (var route in new[] { ReleaseGateEndpoints.ComboRoute,
                     ReleaseGateEndpoints.SelectRoute, ReleaseGateEndpoints.Route })
        {
            using var denied = await Get(app, route, token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        // Even malformed input must be refused as unauthorized, before the body is touched.
        using var write = await PostVerdict(app, token, "not JSON");
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Theory(Timeout = 240_000)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("{\"candidateTag\":\"3.0.0-ci.1\",\"verifiedAt\":\"2026-09-10T01:00:00Z\",\"verdict\":\"Green\"}")]
    public async Task ComboVerification_RefusesAnInvalidOrEmptyGreenVerdict(string body)
    {
        jwks = () => tokens.Jwks();
        await Grant(VerificationPrincipal(), cancellationToken: TestContext.Current.CancellationToken);
        await using var app = await StartHost(Path.GetTempPath());
        using var response = await PostVerdict(app,
            tokens.Mint(Audience, eventName: "workflow_run"), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// 🚨 A retired module (#3848, policy <c>record-served-commit-on-install</c>) is reported APART
    /// from the verified ones: a verdict that lists the same module as verified AND as retired is
    /// internally inconsistent and the recording route refuses it (400), while the same verdict
    /// with a disjoint retired list is recorded — the control that the 400 is about the overlap,
    /// and about nothing else in the body.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ComboVerification_RefusesAModuleBothVerifiedAndRetired_AndRecordsADisjointOne()
    {
        jwks = () => tokens.Jwks();
        await Grant(VerificationPrincipal(), cancellationToken: TestContext.Current.CancellationToken);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateOrUpdateNode(new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
            {
                Name = "Update policy", NodeType = UpdatePolicyNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.None },
            })).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await using var app = await StartHost(Path.GetTempPath());
        var token = tokens.Mint(Audience, eventName: "workflow_run");

        ComboVerification Verdict(string retiredId) => new()
        {
            CandidateTag = "3.0.0-ci.456", VerifiedAt = DateTimeOffset.UtcNow,
            Verdict = ComboVerdictKind.NotVerifiable, Caveats = ["Test could not execute the candidate"],
            Modules =
            [
                new ModuleVerification
                {
                    ModuleId = "Widget", Outcome = ModuleVerificationOutcome.NotVerified,
                    Failures = ["the gate did not run"],
                },
            ],
            RetiredModules = [new RetiredModule { ModuleId = retiredId, Reason = "gone at HEAD" }],
        };

        using var overlapping = await PostVerdict(app, token,
            JsonSerializer.Serialize(Verdict("widget"), InstanceComboAssembler.Json));
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);

        using var disjoint = await PostVerdict(app, token,
            JsonSerializer.Serialize(Verdict("PlatformUI"), InstanceComboAssembler.Json));
        Assert.Equal(HttpStatusCode.OK, disjoint.StatusCode);
        Assert.Contains("\"recorded\":true", await disjoint.Content.ReadAsStringAsync());
    }

    // ── a principal DECLARED in configuration (policy declared-not-derived) ───

    [Fact(Timeout = 240_000)]
    public async Task ADeclaredPrincipal_ReachesTheComboRoutes_WithNoNode_AndOnlyAsDeclared()
    {
        // 🚨 No Grant(...) anywhere in this fact: nothing under Admin/_BuildPrincipal names the
        // repository. The only thing that trusts it is the deployment's configuration.
        jwks = () => tokens.Jwks();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateOrUpdateNode(new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
            {
                Name = "Update policy", NodeType = UpdatePolicyNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.None },
            })).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var designed = tokens.Mint(Audience, repository: DeclaredRepository, eventName: "workflow_run");
        var authenticated = await Authenticate(designed);
        Assert.False(authenticated.IsUnavailable);
        Assert.NotNull(authenticated.Build);
        Assert.Null(authenticated.Instance);
        Assert.Equal($"{DeclaredBuildPrincipals.ConfigSection}:0", authenticated.Build!.PrincipalPath);
        Assert.True(authenticated.Build.Allows(BuildVerbs.Verify, "combo"));
        Assert.False(authenticated.Build.Allows(BuildVerbs.Fetch, Source));

        await using var app = await StartHost(Path.GetTempPath());
        using var combo = await Get(app, ReleaseGateEndpoints.ComboRoute, designed);
        Assert.Equal(HttpStatusCode.OK, combo.StatusCode);

        // The refusal matrix, one input moved at a time from the token that was just admitted.
        foreach (var refused in new[]
                 {
                     tokens.Mint(Audience, repository: DeclaredRepository, eventName: "push"),
                     tokens.Mint(Audience, repository: DeclaredRepository, eventName: "workflow_run",
                         gitRef: "refs/heads/feature"),
                     tokens.Mint(Audience, repository: DeclaredRepository, eventName: "workflow_run",
                         repositoryId: "1"),
                     tokens.Mint(Audience, repository: DeclaredRepository + ".Evil", eventName: "workflow_run"),
                     tokens.Mint("https://another.portal.test", repository: DeclaredRepository,
                         eventName: "workflow_run"),
                 })
        {
            using var denied = await Get(app, ReleaseGateEndpoints.ComboRoute, refused);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // A declaration grants verify:combo and nothing else — the publication routes stay shut.
        // 403, exactly: the declared principal authenticated and was refused for the scope it lacks.
        // Anything else (a 401, a 404, a 500) would mean the request never reached that decision.
        using var fetch = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}", designed);
        Assert.Equal(HttpStatusCode.Forbidden, fetch.StatusCode);
    }

    [Fact(Timeout = 240_000)]
    public async Task ANode_OverridesItsDeclaration_SoARevokeTakesEffectWithoutARoll()
    {
        jwks = () => tokens.Jwks();
        var token = tokens.Mint(Audience, repository: DeclaredRepository, eventName: "workflow_run");
        Assert.NotNull((await Authenticate(token, TestContext.Current.CancellationToken)).Build);

        // A node narrower than the declaration: the NODE decides, so the declared scope is gone.
        await Grant(VerificationPrincipal() with { Repository = DeclaredRepository, Scopes = ["fetch:plugins"] });
        var narrowed = await Authenticate(token);
        Assert.NotNull(narrowed.Build);
        Assert.Equal(BuildPrincipal.PathFor(DeclaredRepository), narrowed.Build!.PrincipalPath);
        Assert.False(narrowed.Build.Allows(BuildVerbs.Verify, "combo"));

        // …and a revoking node stops the declared principal with no roll, on the very next request:
        // the declaration is consulted only once the OWNER has said no node exists, so neither a
        // listing nor a mirror that has not caught up yet can hand back the declared trust — and the
        // refusal is a verdict, not a read that could not be made.
        await Grant(VerificationPrincipal() with
        {
            Repository = DeclaredRepository, RequestedAction = BuildPrincipalActions.Revoke,
        });
        var revoked = await Authenticate(token, TestContext.Current.CancellationToken);
        Assert.False(revoked.IsUnavailable);
        Assert.Null(revoked.Build);
    }

    private static BuildPrincipal VerificationPrincipal() => Principal() with
    {
        RepositoryId = "123456789", RepositoryOwnerId = "9999", Scopes = ["verify:combo"],
        Events = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["workflow_run"] = [BuildVerbs.Verify], ["workflow_dispatch"] = [BuildVerbs.Verify],
        },
        EventRefs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["workflow_run"] = ["refs/heads/main"], ["workflow_dispatch"] = ["refs/heads/main"],
        },
    };

    private static async Task<HttpResponseMessage> PostVerdict(WebApplication app, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ReleaseGateEndpoints.VerificationRoute);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await app.GetTestClient().SendAsync(request);
    }

    private static void WriteBundle(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("meshweaver/manifest.json").Open());
        writer.Write("""{"plugin":"Store"}""");
    }

    private async Task<WebApplication> StartHost(string publishedRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublishedBundleCatalogue.PublishedRootConfigKey] = publishedRoot,
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        builder.Services.AddSingleton(new InstanceComboReader(Mesh));
        var app = builder.Build();
        app.MapPluginBundles();
        app.MapReleaseGate();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> Get(WebApplication app, string route, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return await app.GetTestClient().SendAsync(request);
    }
}
