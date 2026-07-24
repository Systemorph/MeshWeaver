using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Security.Cryptography;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

public class ApiTokenServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private ApiTokenService GetService(TimeSpan? validationReadTimeout = null, ILogger<ApiTokenService>? logger = null) =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            logger ?? Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>()
        )
        {
            // Short read window so the negative-path tests (unknown / revoked-with-
            // deleted-index / deleted token, which wait out the resilient read's
            // timeout by design) stay fast on the single in-memory mesh; a valid
            // token resolves on the first attempt regardless. Prod default is 8 s —
            // same pattern as OAuthCodeStoreTest.NewStore's ReadTimeout.
            ValidationReadTimeout = validationReadTimeout ?? TimeSpan.FromSeconds(2),
        };

    [Fact]
    public async Task CreateToken_ReturnsTokenWithCorrectPrefix()
    {
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "Test Label").Should().Emit();

        result.RawToken.Should().StartWith("mw_");
        result.RawToken.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task CreateToken_StoresNodeWithHashedContent()
    {
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "My Token").Should().Emit();

        result.Node.Should().NotBeNull();
        result.Node.NodeType.Should().Be("ApiToken");
        result.Node.Namespace.Should().Be("user1/ApiToken");

        var apiToken = result.Node.Content as ApiToken;
        apiToken.Should().NotBeNull();
        apiToken!.TokenHash.Should().NotBeNullOrEmpty();
        apiToken.TokenHash.Should().NotContain("mw_");
        apiToken.UserId.Should().Be("user1");
        apiToken.UserName.Should().Be("Test User");
        apiToken.UserEmail.Should().Be("test@example.com");
        apiToken.Label.Should().Be("My Token");
        apiToken.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task CreateToken_PersistsNodeToStorage()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Test").Should().Emit();

        // ApiToken nodes don't activate per-node hubs (IsSatelliteType), so
        // ReadNodeAsync would hang waiting for a route — read via the same
        // mesh-level index ApiTokenService uses internally. Wait on the live
        // query stream until the create lands (absorbs read-side index lag).
        var stored = await ObserveNode(result.Node.Path!).Should().Match(n => n is not null);
        stored.Should().NotBeNull();
        stored!.NodeType.Should().Be("ApiToken");
    }

    [Fact]
    public async Task ValidateToken_ValidToken_ReturnsApiToken()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Valid Token").Should().Emit();

        // ValidateToken reads via the live GetApiTokenByHash synced query,
        // whose first snapshot can be empty right after the create (read-side
        // index lag). Re-issue on a 50 ms interval until the token becomes
        // visible — same primitive as ValidateToken_RevokedToken_ReturnsNull.
        var validated = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is not null);

        validated.Should().NotBeNull();
        validated!.UserId.Should().Be("user1");
        validated.UserName.Should().Be("Test User");
        validated.Label.Should().Be("Valid Token");
    }

    [Fact]
    public async Task ValidateToken_InvalidToken_ReturnsNull()
    {
        var result = await GetService().ValidateToken("mw_invalidtokenvalue123").Should().Emit();

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_EmptyToken_ReturnsNull()
    {
        var result = await GetService().ValidateToken("").Should().Emit();
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_NullToken_ReturnsNull()
    {
        var result = await GetService().ValidateToken(null!).Should().Emit();
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_TokenWithoutPrefix_ReturnsNull()
    {
        var result = await GetService().ValidateToken("notaprefixedtoken").Should().Emit();
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_ExpiredToken_ReturnsNull()
    {
        var logs = new CapturingLogger();
        var service = GetService(logger: logs);
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Expired",
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1)).Should().Emit();

        var validated = await service.ValidateToken(result.RawToken).Should().Emit();

        validated.Should().BeNull();
        // No silent rejection: the failing stage must be named at Warning with the
        // hash prefix (never the raw token) — the diagnosability gap behind the
        // untraceable 401s of 2026-07-24.
        var hashPrefix = ApiTokenService.HashToken(result.RawToken)[..12];
        logs.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("expired") && e.Message.Contains(hashPrefix),
            "an expired-token rejection must log a Warning naming the stage and hash prefix");
        logs.Entries.Should().NotContain(e => e.Message.Contains(result.RawToken),
            "the raw token must never appear in any log line");
    }

    // ── Resilient validation read (the fresh-token multi-replica 401 fix) ──
    //
    // Prod bug being pinned (memex-cloud 2026-07-24, 3+ KEDA replicas): /token minted
    // an mw_ token on pod A; the MCP client's immediate reconnect landed on pods B/C,
    // where ValidateToken's one-shot 5 s GetMeshNode read hit the create→routable
    // window, timed out, and emitted null SILENTLY → 401 for ~2 minutes (ingress:
    // every rejected request took exactly 5.000–5.007 s). The fix mirrors
    // OAuthCodeStore.ReadCodeNode (#620): a self-paced GetMeshNodeStream poll bounded
    // by ValidationReadTimeout, with a hash MISMATCH on a successfully-read node as a
    // terminal fail-fast (never a poll spin).

    /// <summary>
    /// SHOULD-FAIL-IF: the resilient read adds latency to the hot path — a warm
    /// token must resolve on the FIRST poll attempt (well under 1 s), never touch
    /// the retry window. The 2 s test window doubles as the discriminator: a
    /// retried/timed-out read would blow both the wall-clock and the null check.
    /// </summary>
    [Fact]
    public async Task ValidateToken_WarmToken_ResolvesOnFirstAttempt_NoAddedLatency()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Warm").Should().Emit();

        // First validation absorbs any residual create→readable lag (issuance already
        // confirmed both nodes readable, so this normally hits first-attempt too).
        (await service.ValidateToken(result.RawToken).Should().Emit()).Should().NotBeNull();

        var stopwatch = Stopwatch.StartNew();
        var validated = await service.ValidateToken(result.RawToken).Should().Emit();
        stopwatch.Stop();

        validated.Should().NotBeNull();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "a warm token must validate on the first read attempt — the resilient poll may add latency ONLY on a genuine miss");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: an unknown token is rejected INSTANTLY — that is exactly the
    /// behavior that 401ed fresh tokens during the create→routable window on the
    /// non-minting replicas. The resilient read must keep polling for the full
    /// <see cref="ApiTokenService.ValidationReadTimeout"/> before concluding null,
    /// and the timeout must be named at Warning with the hash prefix.
    /// </summary>
    [Fact]
    public async Task ValidateToken_UnknownToken_FailsAtReadTimeout_NotInstantly()
    {
        var logs = new CapturingLogger();
        var window = TimeSpan.FromSeconds(1);
        var service = GetService(window, logs);
        const string unknownToken = "mw_thistokenwasneverissued0123456789";

        var stopwatch = Stopwatch.StartNew();
        var validated = await service.ValidateToken(unknownToken).Should().Emit();
        stopwatch.Stop();

        validated.Should().BeNull();
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(window - TimeSpan.FromMilliseconds(100),
            "an unknown token must poll the full ValidationReadTimeout window — an instant no is indistinguishable from a fresh token that is not yet routable on this replica");

        var hashPrefix = ApiTokenService.HashToken(unknownToken)[..12];
        logs.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("index-read-timeout")
                && e.Message.Contains(hashPrefix),
            "the read timeout must be logged at Warning with the failing stage and hash prefix — never a silent null");
        logs.Entries.Should().NotContain(e => e.Message.Contains(unknownToken),
            "the raw token must never appear in any log line");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: a node that reads SUCCESSFULLY but carries a different full
    /// hash (someone else's token colliding on the 12-char prefix, or tampering)
    /// spins the poll to the timeout. A read that succeeded with non-matching
    /// content is a TERMINAL mismatch — fail fast, log <c>index-hash-mismatch</c>.
    /// </summary>
    [Fact]
    public async Task ValidateToken_ExistingIndexWithWrongHash_FailsFast_NotAtTimeout()
    {
        var logs = new CapturingLogger();
        // Generous window so the timing assert genuinely discriminates fail-fast
        // from wait-out-the-window.
        var service = GetService(TimeSpan.FromSeconds(8), logs);

        var rawToken = "mw_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hashPrefix = ApiTokenService.HashToken(rawToken)[..12];

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var indexNode = new MeshNode(hashPrefix, "ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            Content = new ApiTokenIndex
            {
                TokenHash = new string('0', 64), // NOT rawToken's hash — colliding entry
                TokenPath = $"user1/ApiToken/{hashPrefix}",
            },
        };
        await meshService.CreateNode(indexNode).Should().Emit();
        // Ensure the node is readable BEFORE measuring, so the wall-clock below
        // captures the mismatch verdict, not create→readable lag.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ReadNode(indexNode.Path))
            .Should().Match(n => n is not null);

        var stopwatch = Stopwatch.StartNew();
        var validated = await service.ValidateToken(rawToken).Should().Emit();
        stopwatch.Stop();

        validated.Should().BeNull("a hash mismatch must reject");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4),
            "a successfully-read node with a non-matching hash is a terminal mismatch — it must NOT spin the poll to the 8 s timeout");
        logs.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("index-hash-mismatch")
                && e.Message.Contains(hashPrefix),
            "the terminal mismatch must be logged at Warning with the failing stage and hash prefix");
    }

    [Fact]
    public async Task ValidateToken_FreshLastUsedAt_DoesNotRewriteTheNode()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Stamp Once").Should().Emit();

        // First validation stamps LastUsedAt (null → now). Wait for the async
        // fire-and-forget write to land on the node.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is not null);
        var stamped = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ObserveNode(result.Node.Path!).Take(1))
            .Should().Match(n => (n?.Content as ApiToken)?.LastUsedAt != null);
        var versionAfterStamp = stamped!.Version;

        // A second validation while the recorded LastUsedAt is FRESH must not write
        // the node again — the stamp has display granularity, not per-request. A busy
        // integration's per-request write turned its token into the hottest node on
        // the mesh (prod: version 8939 in one day) and every write fans out through
        // the change feed to all subscriber streams.
        (await service.ValidateToken(result.RawToken).Should().Emit()).Should().NotBeNull();

        // Sanctioned fixed wait: negative "nothing happened" check — there is no positive
        // signal to await for a write that must NOT occur. Sample a few times across the
        // window (not a single read) so a DELAYED wrong write is still caught.
        for (var sample = 0; sample < 3; sample++)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var after = await ObserveNode(result.Node.Path!).Take(1).Should().Match(n => n is not null);
            after!.Version.Should().Be(versionAfterStamp,
                "a fresh LastUsedAt must not be re-stamped on every validation");
        }
    }

    [Fact]
    public async Task ValidateToken_RapidBackToBackValidations_StampOnlyOnce()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Burst Stamp").Should().Emit();

        // First successful validation (poll until the token becomes visible to
        // the read side) dispatches the ONE legitimate LastUsedAt stamp — then
        // immediately BURST more validations back-to-back, deliberately never
        // waiting for the stamp write to land or any read side to catch up.
        // Every read-side surface (query snapshot, stream mirror) may still
        // carry the pre-stamp LastUsedAt here, so a freshness gate keyed on
        // ANY read-side state re-dispatches one stamp per call — the CI
        // failure shape (runs 28682878901 / 28684288201: 5 then 3 duplicate
        // stamps on one token node). The dispatch-time single-flight memo
        // must let exactly one write through.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is not null);
        for (var i = 0; i < 5; i++)
            (await service.ValidateToken(result.RawToken).Should().Emit()).Should().NotBeNull();

        // The deterministic single-flight contract: exactly ONE stamp write was
        // DISPATCHED across the whole burst, no matter how far any read side
        // lagged. (Node-version observation alone can't distinguish one write
        // from several coalesced by the change feed, hence the dispatch counter.)
        service.StampDispatchCount.Should().Be(1,
            "rapid back-to-back validations must dispatch exactly one LastUsedAt stamp");

        // The stamp lands once: capture the written state, then sample across a
        // window (sanctioned fixed wait — negative check with no positive signal)
        // asserting NO further write ever lands: MeshNode.Version only moves when
        // THIS node is written, and every duplicate stamp would carry a DISTINCT
        // LastUsedAt — both must stay frozen.
        var stamped = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ObserveNode(result.Node.Path!).Take(1))
            .Should().Match(n => (n?.Content as ApiToken)?.LastUsedAt != null);
        var stampedVersion = stamped!.Version;
        var stampedLastUsedAt = ((ApiToken)stamped.Content!).LastUsedAt;

        for (var sample = 0; sample < 3; sample++)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var after = await ObserveNode(result.Node.Path!).Take(1).Should().Match(n => n is not null);
            after!.Version.Should().Be(stampedVersion,
                "no lagged read may re-dispatch a stamp while one is already in flight");
            ((ApiToken)after.Content!).LastUsedAt.Should().Be(stampedLastUsedAt,
                "a duplicate stamp would overwrite LastUsedAt with a later timestamp");
        }
    }

    [Fact]
    public async Task ValidateToken_RevokedToken_ReturnsNull()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Revoke").Should().Emit();

        await service.RevokeToken(result.Node.Path).Should().Emit();

        // ApiToken nodes don't activate per-node hubs (see 1e22b3cc3), so
        // ReadNode(path).MeshNodeReference would hang. Verify revoke through
        // the production read path — ValidateToken reads via the
        // mesh-level index that QueryAsync(path:X) uses. Stream-poll the
        // request/response on a 50 ms interval until the token reads as null,
        // absorbing read-side index lag — replaces a hand-rolled do/while +
        // Task.Delay(50).
        var validated = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is null);

        validated.Should().BeNull();
    }

    [Fact]
    public async Task RevokeToken_ExistingToken_ReturnsTrue()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Revoke").Should().Emit();

        var revoked = await service.RevokeToken(result.Node.Path).Should().Emit();

        revoked.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeToken_NonexistentPath_ReturnsFalse()
    {
        var revoked = await GetService().RevokeToken("ApiToken/nonexistent").Should().Emit();

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task GetTokensForUser_ReturnsOnlyUserTokens()
    {
        var service = GetService();
        await service.CreateToken("user1", "User One", "u1@test.com", "Token A").Should().Emit();
        await service.CreateToken("user1", "User One", "u1@test.com", "Token B").Should().Emit();
        await service.CreateToken("user2", "User Two", "u2@test.com", "Token C").Should().Emit();

        var user1Tokens = await service.GetTokensForUser("user1").Should().Match(t => t.Count == 2);
        var user2Tokens = await service.GetTokensForUser("user2").Should().Match(t => t.Count == 1);

        user1Tokens.Should().HaveCount(2);
        user1Tokens.Select(t => t.Label).Should().Contain("Token A").And.Contain("Token B");

        user2Tokens.Should().HaveCount(1);
        user2Tokens[0].Label.Should().Be("Token C");
    }

    [Fact]
    public async Task GetTokensForUser_NeverExposesFullHash()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Test").Should().Emit();

        var tokens = await service.GetTokensForUser("user1").Should().Match(t => t.Count == 1);

        tokens.Should().HaveCount(1);
        var tokenInfo = tokens[0];
        tokenInfo.HashPrefix.Length.Should().BeLessThanOrEqualTo(8);
        var fullHash = ApiTokenService.HashToken(result.RawToken);
        fullHash.Length.Should().Be(64);
        tokenInfo.HashPrefix.Should().Be(fullHash[..8]);
    }

    // ── DeleteToken (previously zero coverage; the old hub.Post pattern would deadlock) ──

    [Fact]
    public async Task DeleteToken_RemovesNodeFromStorage()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Delete").Should().Emit();

        await service.DeleteToken(result.Node.Path).Should().Emit();

        // ApiToken nodes don't activate per-node hubs — read via the same
        // mesh-level path:X query the production service uses, waiting on the
        // live stream to absorb read-side index lag.
        var stored = await ObserveNode(result.Node.Path).Should().Match(n => n is null);
        stored.Should().BeNull();
    }

    [Fact]
    public async Task DeleteToken_AfterDelete_ValidateReturnsNull()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Delete").Should().Emit();

        await service.DeleteToken(result.Node.Path).Should().Emit();

        // Stream-poll the request/response until null — replaces a hand-rolled
        // do/while + Task.Delay(50) loop.
        var validated = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is null);

        validated.Should().BeNull();
    }

    [Fact]
    public async Task DeleteToken_AlsoRemovesIndexEntry()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Index Delete").Should().Emit();

        var apiToken = result.Node.Content as ApiToken;
        var hashPrefix = apiToken!.TokenHash[..12];
        var indexPath = $"ApiToken/{hashPrefix}";

        await service.DeleteToken(result.Node.Path).Should().Emit();

        // Same primitive as DeleteToken_RemovesNodeFromStorage — query, not
        // ReadNodeAsync (no per-node hub at ApiToken/{hash} either).
        var index = await ObserveNode(indexPath).Should().Match(n => n is null);
        index.Should().BeNull();
    }

    [Fact]
    public async Task DeleteToken_NonexistentPath_Completes()
    {
        var result = await GetService().DeleteToken("user1/ApiToken/nonexistent").Should().Emit();
        result.Should().BeFalse();
    }

    // ── GetTokensForUser edge cases ──

    [Fact]
    public async Task GetTokensForUser_EmptyUser_ReturnsEmpty()
    {
        var tokens = await GetService().GetTokensForUser("nobody").Should().Emit();

        tokens.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTokensForUser_RevokedToken_StillAppearsAsRevoked()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Revoke Me").Should().Emit();
        await service.RevokeToken(result.Node.Path).Should().Emit();

        // 🚨 Subscribe ONCE to the live synced-query stream and wait for the
        // condition via Should().Match(...). Replaces the prior
        // Observable.Interval(50ms) polling — re-subscribing to GetTokensForUser
        // every tick raced the synced-query Replay(1) cache (project_synced_query_race.md):
        // each poll hit the cache's buffered Initial snapshot rather than waiting
        // for the live Updated emission from the revoke's NotifyChange.
        var tokens = await service.GetTokensForUser("user1")
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(t => t.Any(x => x.Label == "Revoke Me" && x.IsRevoked));

        tokens.Should().ContainSingle(t => t.Label == "Revoke Me" && t.IsRevoked);
    }

    [Fact]
    public async Task GetTokensForUser_DeletedToken_DoesNotAppear()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Delete Me").Should().Emit();
        await service.DeleteToken(result.Node.Path).Should().Emit();

        // Single live subscription to the synced-query stream — see comment on
        // GetTokensForUser_RevokedToken_StillAppearsAsRevoked for why polling
        // re-subscriptions race the Replay(1) cache.
        var tokens = await service.GetTokensForUser("user1")
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(t => !t.Any(x => x.Label == "Delete Me"));

        tokens.Should().NotContain(t => t.Label == "Delete Me");
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        var hash1 = ApiTokenService.HashToken("mw_testtoken123");
        var hash2 = ApiTokenService.HashToken("mw_testtoken123");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashToken_ProducesSha256HexString()
    {
        var hash = ApiTokenService.HashToken("mw_testtoken123");

        hash.Length.Should().Be(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void HashToken_DifferentTokens_ProduceDifferentHashes()
    {
        var hash1 = ApiTokenService.HashToken("mw_token1");
        var hash2 = ApiTokenService.HashToken("mw_token2");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public async Task CreateToken_WithExpiry_SetsExpiresAt()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "Expiring",
            expiresAt: expiry).Should().Emit();

        var apiToken = result.Node.Content as ApiToken;
        apiToken!.ExpiresAt!.Value.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateToken_WithoutExpiry_ExpiresAtIsNull()
    {
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "No Expiry").Should().Emit();

        var apiToken = result.Node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateToken_EachTokenIsUnique()
    {
        var service = GetService();
        var result1 = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Token 1").Should().Emit();
        var result2 = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Token 2").Should().Emit();

        result1.RawToken.Should().NotBe(result2.RawToken);
    }

    [Fact]
    public async Task ValidateToken_FutureExpiry_ReturnsApiToken()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Future",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30)).Should().Emit();

        // Live synced-query read — re-issue until the just-created token
        // lands (absorbs read-side index lag).
        var validated = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is not null);

        validated.Should().NotBeNull();
        validated!.UserId.Should().Be("user1");
    }

    /// <summary>
    /// Folds the live <see cref="IMeshService.Query"/> stream for the MeshNode at
    /// <paramref name="path"/> into a single <c>MeshNode?</c> — Initial / Added / Updated /
    /// Removed events collapse via <c>.Scan</c>. Callers assert on the result with
    /// <c>.Should().Match(...)</c>, which blocks (≤ the assertion timeout) for the first
    /// snapshot satisfying the predicate. No polling, no <c>Task.Delay</c>.
    /// <para>
    /// Used for ApiToken paths as a route-independent read: the mesh-level <c>Query</c> reads
    /// the authoritative persistence state with live change-notifier deltas instead of a
    /// polling loop. (ApiToken nodes are regular content nodes — <c>IsSatelliteType = false</c>
    /// per <c>ApiTokenNodeType.CreateMeshNode</c> — so <c>GetMeshNodeStream(path)</c> also works;
    /// production <c>ValidateToken</c> reads through it since the resilient-read fix.)
    /// </para>
    /// </summary>
    private IObservable<MeshNode?> ObserveNode(string path)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}", WellKnownUsers.System))
            .Scan((MeshNode?)null, (current, change) => change.ChangeType switch
            {
                QueryChangeType.Initial or QueryChangeType.Reset =>
                    change.Items.FirstOrDefault(),
                QueryChangeType.Added or QueryChangeType.Updated =>
                    change.Items.FirstOrDefault() ?? current,
                QueryChangeType.Removed => null,
                _ => current,
            });
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    /// <summary>
    /// Minimal in-memory <see cref="ILogger{T}"/> capture so the tests can assert the
    /// no-silent-401 contract (every validation failure logs a Warning naming the
    /// failing stage + hash prefix, never the raw token). Instance state on the test —
    /// dies with it; not a mock of any core mesh interface.
    /// </summary>
    private sealed class CapturingLogger : ILogger<ApiTokenService>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
