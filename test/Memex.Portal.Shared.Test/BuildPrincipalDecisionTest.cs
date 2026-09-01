using System;
using System.Collections.Generic;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The AUTHORIZATION half of the build principal (#2483) — the rule a verified GitHub Actions token
/// is decided against, once the signature has established only WHICH repository asked.
///
/// <para>The three steps the design names, pinned one at a time: <c>repository</c> claim → node,
/// <c>event_name</c> claim → allowed verbs, requested <c>verb:source</c> ∈ <c>scopes</c>. Every
/// refusal below starts from the allowed case and moves exactly one thing, so a passing assertion
/// can only mean that one thing was checked.</para>
/// </summary>
public class BuildPrincipalDecisionTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "Systemorph/MeshWeaver.SocialMedia";

    private static BuildPrincipal Principal(
        string repository = Repository,
        string[]? scopes = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? events = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? eventRefs = null) =>
        new()
        {
            Repository = repository,
            Scopes = scopes ?? ["publish:socialmedia", "fetch:plugins"],
            Events = events ?? new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["push"] = ["publish", "fetch"],
                ["pull_request"] = ["fetch"],
            },
            EventRefs = eventRefs ?? new Dictionary<string, IReadOnlyCollection<string>>(),
            IssuedBy = "admin",
            IssuedAt = Now.AddDays(-1),
        };

    private static GitHubBuildClaims Claims(
        string repository = Repository,
        string eventName = "push",
        string gitRef = "refs/heads/main",
        string? repositoryId = "123456789") =>
        new()
        {
            Repository = repository,
            RepositoryId = repositoryId,
            RepositoryOwnerId = "9999",
            EventName = eventName,
            Ref = gitRef,
        };

    [Fact]
    public void TheDesignedCase_Allows()
    {
        Assert.True(Principal().Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.True(Principal().Allows(Claims(), BuildVerbs.Publish, "socialmedia", Now));
    }

    [Fact]
    public void AnotherRepositorysToken_IsRefused()
    {
        // 🚨 THE assertion this whole file exists for. Every workflow on GitHub holds a token with a
        // valid signature; only the repository claim tells them apart. A prefix or wildcard match
        // here would authorize any repository whose name merely starts the same way.
        Assert.False(Principal().Allows(Claims("Systemorph/MeshWeaver.Evil"), BuildVerbs.Fetch, "plugins", Now));
        Assert.False(Principal().Allows(Claims("Evil/MeshWeaver.SocialMedia"), BuildVerbs.Fetch, "plugins", Now));
        Assert.Contains("repository", Principal().Refuse(
            Claims("Systemorph/MeshWeaver.Evil"), BuildVerbs.Fetch, "plugins", Now)!);
    }

    [Fact]
    public void TheImmutableClaimFormatStillMatchesAClassicallyWrittenPrincipal()
    {
        // The migration hazard: GitHub moving the org to immutable ids must not silently stop every
        // build principal in the fleet from authenticating.
        Assert.True(Principal().Allows(
            Claims("Systemorph@12345/MeshWeaver.SocialMedia@67890"), BuildVerbs.Fetch, "plugins", Now));
    }

    [Fact]
    public void AnUnlistedEvent_IsRefused()
    {
        Assert.False(Principal().Allows(Claims(eventName: "workflow_dispatch"), BuildVerbs.Fetch, "plugins", Now));
        Assert.False(Principal().Allows(Claims(eventName: ""), BuildVerbs.Fetch, "plugins", Now));
    }

    [Fact]
    public void AVerbTheEventDoesNotCarry_IsRefused()
    {
        // The design's whole point: `push` on main may publish; a pull request may only fetch.
        Assert.True(Principal().Allows(Claims(eventName: "pull_request"), BuildVerbs.Fetch, "plugins", Now));
        Assert.False(Principal().Allows(
            Claims(eventName: "pull_request"), BuildVerbs.Publish, "socialmedia", Now));
    }

    [Fact]
    public void AScopeForAnotherSource_IsRefused_AndThereIsNoWildcard()
    {
        Assert.False(Principal().Allows(Claims(), BuildVerbs.Fetch, "education", Now));
        // A principal cannot buy itself every source by writing a star: `fetch:*` is a literal
        // scope for a source literally named "*", which no registry has.
        Assert.False(Principal(scopes: ["fetch:*"]).Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        // …and publishing is not implied by fetching.
        Assert.False(Principal(scopes: ["fetch:plugins"]).Allows(Claims(), BuildVerbs.Publish, "plugins", Now));
    }

    [Fact]
    public void AScopeIsCaseInsensitiveOnBothHalves()
    {
        // Source names are operator-typed config values, matched case-insensitively everywhere else
        // in the grant model (PluginGrantEntry.Source) — this must not be the one place they are not.
        Assert.True(Principal(scopes: ["Fetch:Plugins"]).Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.True(Principal().Allows(Claims(), "FETCH", "PLUGINS", Now));
    }

    [Fact]
    public void ARefPin_AppliesToTheEventThatDeclaresIt_AndOnlyThat()
    {
        var pinned = Principal(eventRefs: new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["push"] = ["refs/heads/main"],
        });

        Assert.True(pinned.Allows(Claims(), BuildVerbs.Publish, "socialmedia", Now));
        // A push to somebody's feature branch is not a push to main.
        Assert.False(pinned.Allows(
            Claims(gitRef: "refs/heads/feat/anything"), BuildVerbs.Publish, "socialmedia", Now));
        // The pull_request event declares no pin — its ref is refs/pull/<n>/merge and cannot be
        // enumerated in advance, so it must stay unconstrained rather than silently refuse.
        Assert.True(pinned.Allows(
            Claims(eventName: "pull_request", gitRef: "refs/pull/17/merge"), BuildVerbs.Fetch, "plugins", Now));
    }

    [Fact]
    public void RevocationTakesEffectImmediately_OnEitherForm()
    {
        // 🚨 No watcher stands between writing the revoke and the refusal. A security stop that
        // waits for a reactor is a security stop with a window.
        Assert.False((Principal() with { RequestedAction = BuildPrincipalActions.Revoke })
            .Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.False((Principal() with { RequestedAction = "revoke" })
            .Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.False((Principal() with { IsRevoked = true })
            .Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
    }

    [Fact]
    public void AnEndedTerm_IsRefused()
    {
        Assert.False((Principal() with { ExpiresAt = Now.AddMinutes(-1) })
            .Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.True((Principal() with { ExpiresAt = Now.AddMinutes(1) })
            .Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
    }

    [Fact]
    public void AnImmutableIdPin_MustMatchExactly_WhenSet()
    {
        var pinned = Principal() with { RepositoryId = "123456789" };

        Assert.True(pinned.Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        // A repository renamed away and its name re-registered by someone else carries a different
        // immutable id — which is the entire reason the pin exists.
        Assert.False(pinned.Allows(Claims(repositoryId: "987654321"), BuildVerbs.Fetch, "plugins", Now));
        Assert.False(pinned.Allows(Claims(repositoryId: null), BuildVerbs.Fetch, "plugins", Now));
        // Unpinned stays unpinned — the ordinary case must not start demanding an id.
        Assert.True(Principal().Allows(Claims(repositoryId: null), BuildVerbs.Fetch, "plugins", Now));
    }

    [Fact]
    public void AnEmptyPrincipal_GrantsNothing()
    {
        // The default-constructed shape — what a half-written node deserializes to — must authorize
        // nothing at all rather than everything.
        var empty = new BuildPrincipal { Repository = Repository };

        Assert.False(empty.Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.False(empty.Allows(null, BuildVerbs.Fetch, "plugins", Now));
        Assert.False(Principal().Allows(Claims(), "", "plugins", Now));
        Assert.False(Principal().Allows(Claims(), BuildVerbs.Fetch, "", Now));
    }
}
