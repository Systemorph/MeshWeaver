using System;
using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The platform-side regression guard for MeshWeaver#2621: every Microsoft sign-in on
/// memex-cloud answered 500 because the tenant arrived as an EMPTY STRING.</b>
///
/// <para>An environment variable or configMap entry cannot be null, only empty — so <c>""</c> is
/// the deployed shape of "unset". The chart rendered <c>Authentication__Microsoft__TenantId</c>
/// with an empty default and the composition read that empty string as the tenant: the authority
/// became <c>login.microsoftonline.com//v2.0</c>, its discovery document could never be fetched
/// (<c>IDX20803</c>), and the failure surfaced as an unhandled <c>InvalidOperationException</c> on
/// every request that touched the handler — never at startup, and never naming the key an operator
/// had to set.</para>
///
/// <para>Both directions are pinned, because neither implies the other: a blank tenant is unset
/// (so a portal that never wired Microsoft sign-in still starts — aborting on an unconfigured
/// OPTIONAL integration is the #2510 failure), and a value that CANNOT form an authority is
/// refused at boot with the key in the message, rather than composing a URL Entra never serves.
/// The <c>//</c> that was the outage's signature can never leave the composer at all.</para>
///
/// <para>The sign-in handler that actually 500-ed lives in the portal package (plugins repo,
/// <c>MicrosoftTenantBlankIsUnsetTest</c> pins it there). This pins the platform-side composer —
/// <see cref="MicrosoftTenant"/>, used by the Executive Assistant's delegated Graph flow — and the
/// boot-time guard <c>MemexConfiguration</c> runs on every portal host.</para>
/// </summary>
public class MicrosoftTenantAuthorityTest
{
    /// <summary>THE defect: the deployed shape of "unset" must never become a tenant.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void ABlankTenantIsUnset(string? configured)
    {
        Assert.Equal(MicrosoftTenant.Unset, MicrosoftTenant.Resolve(configured));
        Assert.Equal("https://login.microsoftonline.com/common/v2.0",
            MicrosoftTenant.Authority(configured, "v2.0"));
    }

    /// <summary>
    /// The outage's literal signature: whatever the tenant is, the composed authority never
    /// carries an empty segment. This is the assertion that would have failed on 2026-08-28.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("organizations")]
    public void TheAuthorityNeverCarriesAnEmptySegment(string? configured)
    {
        Assert.DoesNotContain("com//", MicrosoftTenant.Authority(configured, "v2.0"), StringComparison.Ordinal);
        Assert.DoesNotContain("com//", MicrosoftTenant.Authority(configured, "oauth2/v2.0"), StringComparison.Ordinal);
    }

    /// <summary>The other direction: a real tenant composes its authority, whitespace-trimmed.</summary>
    [Theory]
    [InlineData("organizations", "https://login.microsoftonline.com/organizations/v2.0")]
    [InlineData(" organizations ", "https://login.microsoftonline.com/organizations/v2.0")]
    [InlineData("consumers", "https://login.microsoftonline.com/consumers/v2.0")]
    [InlineData("contoso.onmicrosoft.com", "https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0")]
    [InlineData("72f988bf-86f1-41af-91ab-2d7cd011db47",
        "https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0")]
    public void AnExplicitTenantFormsItsAuthority(string configured, string expected)
    {
        Assert.Equal(expected, MicrosoftTenant.Authority(configured, "v2.0"));
    }

    /// <summary>The Executive Assistant's delegated Graph flow composes the same way, one path deeper.</summary>
    [Fact]
    public void TheGraphAuthorityIsComposedFromTheSameTenant()
    {
        Assert.Equal("https://login.microsoftonline.com/organizations/oauth2/v2.0",
            MicrosoftTenant.Authority("organizations", "oauth2/v2.0"));
    }

    /// <summary>
    /// A value that is not a single authority segment is refused, and the refusal NAMES the
    /// configuration key an operator has to set — the whole point of the guard.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("a/b")]
    [InlineData("my tenant")]
    [InlineData("../common")]
    [InlineData("organizations?x=1")]
    [InlineData("login.microsoftonline.com/organizations")]
    public void AMalformedTenantIsRefusedByName(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MicrosoftTenant.Authority(configured, "v2.0"));
        Assert.Contains(MicrosoftTenant.EnvironmentKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains(configured, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The boot-time guard: it refuses a malformed tenant at STARTUP (where
    /// <c>MemexConfiguration.ConfigureMemexMesh</c> calls it) and — just as importantly — passes an
    /// unset one, so a portal that never wired Microsoft sign-in still starts.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("organizations")]
    public void ValidateAcceptsEveryUsableConfiguration(string? configured)
    {
        MicrosoftTenant.Validate(configured);
    }

    /// <summary>…and the same guard refuses the value that cannot work, naming the key.</summary>
    [Fact]
    public void ValidateRefusesAMalformedTenantAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MicrosoftTenant.Validate("a/b"));
        Assert.Contains(MicrosoftTenant.EnvironmentKey, ex.Message, StringComparison.Ordinal);
    }
}
