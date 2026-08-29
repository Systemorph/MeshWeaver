using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression tests for the permission-probe cache key
/// (<c>MeshNodeStreamCache.BuildAccessCacheKey</c>) — MeshWeaver.SocialMedia#92, reported as
/// "MCP token cannot read Profiles nodes although the portal renders them for the same user".
///
/// <para><b>The mechanism.</b> <c>PermissionEvaluator.GetEffectivePermissions</c> is NOT a
/// function of <c>(path, userId)</c>. It also reads the caller's <see cref="AccessContext"/>:
/// <see cref="AccessContext.IsApiToken"/> together with the token's claim roles drive the
/// API-token capability clamp (which zeroes the WHOLE permission set — not merely the
/// <c>Api</c> bit — when a bearer context lacks <c>Api</c>), and <see cref="AccessContext.IsHub"/>
/// drives the hub-credential early return. The probe cache in front of it was keyed by
/// <c>(path, userId)</c> alone, and <c>IMeshNodeStreamCache</c> is a process-wide SINGLETON, so a
/// person's browser session and their MCP token really do meet in one entry.</para>
///
/// <para><b>Both directions are wrong ANSWERS, not errors</b> — which is why nothing logged and
/// neither surface reproduced the other's result:</para>
/// <list type="bullet">
/// <item><b>Too permissive — a capability bypass.</b> A portal read caches the unclamped
/// permissions; a bearer read of the same node inside the TTL takes that entry and never runs
/// the clamp at all, admitting a token to the API surface the clamp exists to keep it off.</item>
/// <item><b>Too restrictive — the reported symptom.</b> A bearer read the clamp zeroed caches
/// <c>Permission.None</c>; the same user's PORTAL read inside the TTL is then refused with
/// "lacks Read permission" on a node they are plainly granted.</item>
/// </list>
///
/// <para>These tests pin the invariant the key must satisfy: <b>it carries every context
/// dimension the evaluation reads, and nothing else</b> — differing where the verdict can
/// differ, and still SHARING an entry where it cannot, or the fix would simply disable the
/// cache.</para>
/// </summary>
public class PermissionProbeCacheKeyTests
{
    private const string Path = "Profiles/RobertLinkedIn";

    private static AccessContext Carson() => new()
    {
        ObjectId = "carson",
        Name = "Carson",
        Roles = ["Viewer"],
    };

    [Fact]
    public void A_bearer_context_and_a_portal_context_never_share_an_entry()
    {
        var portal = Carson();
        var bearer = Carson() with { IsApiToken = true };

        Assert.NotEqual(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, portal),
            MeshNodeStreamCache.BuildAccessCacheKey(Path, bearer));
    }

    [Fact]
    public void A_hub_credential_never_shares_an_entry_with_a_user_context()
    {
        var user = Carson();
        var hub = Carson() with { IsHub = true };

        Assert.NotEqual(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, user),
            MeshNodeStreamCache.BuildAccessCacheKey(Path, hub));
    }

    /// <summary>
    /// Two tokens for the SAME person whose claims differ are two different capabilities — one
    /// may carry <c>Api</c> and the other not — so one token's verdict must never be served to
    /// the other.
    /// </summary>
    [Fact]
    public void Two_tokens_with_different_claim_roles_never_share_an_entry()
    {
        var withViewer = Carson() with { IsApiToken = true, Roles = ["Viewer"] };
        var withNothing = Carson() with { IsApiToken = true, Roles = [] };

        Assert.NotEqual(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, withViewer),
            MeshNodeStreamCache.BuildAccessCacheKey(Path, withNothing));
    }

    /// <summary>
    /// …but claims are a SET. Two tokens carrying the same roles in a different order are the
    /// same capability and must still share the entry — otherwise the fix quietly turns the
    /// probe cache off for every multi-role token, which is a latency regression on the read
    /// path rather than a security fix.
    /// </summary>
    [Fact]
    public void Claim_role_ORDER_does_not_split_the_cache()
    {
        var a = Carson() with { IsApiToken = true, Roles = ["Viewer", "Editor"] };
        var b = Carson() with { IsApiToken = true, Roles = ["Editor", "Viewer"] };

        Assert.Equal(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, a),
            MeshNodeStreamCache.BuildAccessCacheKey(Path, b));
    }

    /// <summary>The cache must still WORK: an identical context on the same path is one entry.</summary>
    [Fact]
    public void An_identical_context_shares_its_entry()
    {
        Assert.Equal(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, Carson()),
            MeshNodeStreamCache.BuildAccessCacheKey(Path, Carson()));
    }

    [Fact]
    public void The_path_and_the_user_still_separate_entries()
    {
        var carson = Carson();
        Assert.NotEqual(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, carson),
            MeshNodeStreamCache.BuildAccessCacheKey("Profiles/RolandLinkedIn", carson));
        Assert.NotEqual(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, carson),
            MeshNodeStreamCache.BuildAccessCacheKey(Path, carson with { ObjectId = "rbuergi" }));
    }

    /// <summary>
    /// Fields the evaluation does NOT read must not split the cache — a key that varied with,
    /// say, the display name would be correct-but-useless, and this test is what keeps the key
    /// honest as <see cref="AccessContext"/> grows.
    /// </summary>
    [Fact]
    public void Irrelevant_context_fields_do_not_split_the_cache()
    {
        var carson = Carson();
        Assert.Equal(
            MeshNodeStreamCache.BuildAccessCacheKey(Path, carson),
            MeshNodeStreamCache.BuildAccessCacheKey(
                Path, carson with { Name = "Carson N.", Email = "carson@example.com" }));
    }
}
