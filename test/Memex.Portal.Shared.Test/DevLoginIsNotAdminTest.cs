using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the rule that a dev-login identity is an ORDINARY USER unless configuration says otherwise.
///
/// <para>🚨 Why this is security-relevant and not a dev-only nicety: <c>ProvisionDevUser</c> used to
/// onboard every dev identity with <c>Role: Admin</c>. That role becomes a CLAIM, and
/// <c>PermissionEvaluator.ComputeRoleState</c> adds claim roles AFTER the per-scope deny
/// subtraction — so a claim-Admin cannot be removed by any gating deny. The visible effect was a
/// paywall BYPASS that read as a gating bug: a signed-in user saw the CHF 900 checkout (entitlement
/// is a record, and they had none) yet still read the gated lessons. Anonymous stayed blocked,
/// because it carries no claim role — which is precisely why it went unnoticed.</para>
///
/// <para>It also made the e2e suite unable to prove the purchase funnel at all: every identity it
/// minted passed every gate, so the gating specs asserted nothing.</para>
/// </summary>
public class DevLoginIsNotAdminTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoConfiguredList_MeansNobodyIsAdmin(string? configured)
    {
        // The DEFAULT is the property that matters: a dev portal that hands out admin by default
        // cannot test any access rule, because every identity passes every gate.
        Assert.False(DevAuthController.IsDevAdmin(configured, "e2e-admin"));
        Assert.False(DevAuthController.IsDevAdmin(configured, "roland"));
    }

    [Fact]
    public void OnlyTheNamedUsersAreAdmin()
    {
        const string list = "e2e-admin,ops";

        Assert.True(DevAuthController.IsDevAdmin(list, "e2e-admin"));
        Assert.True(DevAuthController.IsDevAdmin(list, "ops"));
        // The locked-out fixtures the gating specs rely on MUST stay ordinary users.
        Assert.False(DevAuthController.IsDevAdmin(list, "e2e-locked"));
        Assert.False(DevAuthController.IsDevAdmin(list, "e2e-user"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndTolerantOfSpacing()
    {
        // The list is hand-written in compose/env files; whitespace must not silently drop an admin
        // (which would break a harness bootstrap in a way that looks like a permissions bug).
        Assert.True(DevAuthController.IsDevAdmin(" e2e-admin , ops ", "E2E-Admin"));
        Assert.True(DevAuthController.IsDevAdmin("E2E-ADMIN", "e2e-admin"));
    }

    [Fact]
    public void APrefixOrSubstringIsNotAMatch()
    {
        // "e2e-admin" must not make "e2e-admin2" — or a bare "e2e" — an admin.
        Assert.False(DevAuthController.IsDevAdmin("e2e-admin", "e2e-admin2"));
        Assert.False(DevAuthController.IsDevAdmin("e2e-admin", "e2e"));
        Assert.False(DevAuthController.IsDevAdmin("e2e", "e2e-admin"));
    }

    [Fact]
    public void TheRuleIsPositionIndependent_SoItCanRunOnEverySignin()
    {
        // Regression: the grant first lived inside ProvisionDevUser, which runs ONLY when the User
        // node is missing. On any mesh whose database already held the user — a re-boot reusing the
        // volume, a seeded user, the second run of a suite — the configured admin silently came back
        // as an ordinary user and the e2e bootstrap hung on an admin-gated Plugin Catalog it could
        // no longer read. The rule must therefore be a pure function of (list, username) with no
        // dependence on whether this is the user's first signin, so it is safe to evaluate every
        // time; the caller now does exactly that.
        const string list = "e2e-admin";
        Assert.True(DevAuthController.IsDevAdmin(list, "e2e-admin"));
        Assert.True(DevAuthController.IsDevAdmin(list, "e2e-admin"));   // second signin: unchanged
        Assert.False(DevAuthController.IsDevAdmin(list, "e2e-locked"));
        Assert.False(DevAuthController.IsDevAdmin(list, "e2e-locked")); // and still not an admin
    }

    [Fact]
    public void AnEmptyUsernameIsNeverAdmin()
    {
        // Guards the degenerate case where a blank id would otherwise match a stray empty entry.
        Assert.False(DevAuthController.IsDevAdmin("e2e-admin,,", ""));
        Assert.False(DevAuthController.IsDevAdmin(",", ""));
    }
}
