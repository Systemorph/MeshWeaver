using System;
using System.Collections.Generic;
using System.Linq;
using Memex.Portal.Shared.Email;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The regression guard for MeshWeaver#2636 / #2637: the memex portal said mail was ON and
/// dropped every message.</b>
///
/// <para><c>Email:Enabled=true</c> with <c>Email:TenantId</c> and <c>Email:ClientId</c> unset. The
/// portal started, served, answered <c>/health</c> 200 — and <c>OutboundEmailSender</c> and
/// <c>InvitationEmailSender</c> both refused to start, so every invitation, notification and
/// document share queued as <c>New</c> and stayed there. <see cref="EmailDeliveryGuard"/> did
/// exactly its job (nothing was ever falsely stamped <c>Sent</c>), but its whole output is one
/// <c>Error</c> line per host start, and a log line nobody reads is not a signal. The install was
/// half-configured for as long as it took a human to notice mail had not arrived.</para>
///
/// <para><b>Both directions are pinned, and neither implies the other.</b> A guard that refuses
/// everything satisfies half of this file and would abort every local dev run and every deployment
/// that never wanted mail — which is #2510, the incident where a half-configured OPTIONAL
/// integration took the whole host down. A guard that refuses nothing satisfies the other half and
/// is the bug being fixed. So:</para>
/// <list type="number">
/// <item><description><b>Absent / <c>Enabled=false</c> starts.</b> Blank is what "no mail on this
/// install" looks like; it is never a misconfiguration.</description></item>
/// <item><description><b><c>Enabled=true</c> and complete starts</b> — both credential flows.</description></item>
/// <item><description><b><c>Enabled=true</c> and incomplete REFUSES</b>, naming every missing key
/// in the binder form the docs use AND the <c>__</c> form an operator actually
/// sets.</description></item>
/// </list>
///
/// <para>Every case is a pure call on inert configuration data — no host, no container, no
/// credential object. That is not test convenience: it is the property #2510 bought (the verdict
/// must be unable to throw for any reason other than the one it reports), and a test that needed a
/// host to reach it would not be pinning it.</para>
/// </summary>
public class EmailStartupConfigurationGuardTest
{
    /// <summary>The memex install from the two incident tickets: switched on, the client secret
    /// present (the refusal named only the other two), tenant and client id unset.</summary>
    private static EmailOptions TheIncident() => new()
    {
        Enabled = true,
        MailboxAddress = "memex@example.test",
        ClientSecret = "secret",
        // TenantId / ClientId deliberately unset — this is the half-configured deployment.
    };

    private static EmailOptions Complete() => new()
    {
        Enabled = true,
        MailboxAddress = "memex@example.test",
        TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        ClientId = "client",
        ClientSecret = "secret",
    };

    // ───────────── rule 1: never configured must NOT refuse (this is the #2510 half) ─────────────

    /// <summary>
    /// 🚨 The case that protects against #2510, stated at its weakest point: NOTHING configured at
    /// all. An absent section binds to <c>null</c>, and a null section is not a claim — it is every
    /// local dev run, every test host and every deployment that never wanted mail. Refusing it
    /// would abort hosts that are working perfectly.
    /// </summary>
    [Fact]
    public void AnAbsentEmailSection_IsNeverRefused()
    {
        EmailConfigurationGuard.Validate((EmailOptions?)null);
        Assert.Empty(EmailConfigurationGuard.MissingRequiredKeys(null));
    }

    /// <summary>
    /// <c>Email:Enabled=false</c> is a COMPLETE, supported configuration however blank the rest of
    /// the section is — that is precisely what the switch means, and the host registers
    /// <c>NoOpEmailSender</c> for it. Asserted over the shapes a real deployment leaves behind: all
    /// blank, half-filled (someone started wiring it and turned it off), and fully filled but
    /// switched off (mail deliberately parked).
    /// </summary>
    [Theory]
    [InlineData("", "", "", "")]
    [InlineData("memex@example.test", "", "", "secret")]
    [InlineData("memex@example.test", "tenant", "client", "secret")]
    public void ADisabledEmailSection_IsNeverRefused_HoweverBlankOrFullItIs(
        string mailbox, string tenantId, string clientId, string clientSecret)
    {
        var options = new EmailOptions
        {
            Enabled = false,
            MailboxAddress = mailbox,
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = clientSecret,
        };

        EmailConfigurationGuard.Validate(options);
        Assert.Empty(EmailConfigurationGuard.MissingRequiredKeys(options));
    }

    /// <summary>
    /// The same rule through the seam the portal actually uses — the bound configuration, not a
    /// hand-built record. An environment that never set a single <c>Email__*</c> variable must pass
    /// the boot guard, and so must one that explicitly switched mail off.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    public void ConfigurationWithNoEmailClaim_PassesTheBootGuard(string? enabled)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Graph:Storage:Type"] = "FileSystem",
        };
        if (enabled is not null)
            settings["Email__Enabled"] = enabled;

        EmailConfigurationGuard.Validate(Configuration(settings));
    }

    // ───────────── rule 2: enabled + incomplete REFUSES, by name ─────────────

    /// <summary>
    /// 🚨 The incident itself: enabled, client secret set, tenant and client id unset. Refused, and
    /// the refusal names BOTH missing keys in BOTH forms — the binder form the documentation and
    /// the code speak, and the <c>__</c> form that goes into a Helm value or a configMap. It must
    /// NOT name the key that is set: a refusal that lists everything tells an operator nothing.
    /// </summary>
    [Fact]
    public void TheIncidentConfiguration_IsRefusedAtStartup_NamingBothMissingKeysInBothForms()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmailConfigurationGuard.Validate(TheIncident()));

        Assert.Contains("Email:TenantId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Email__TenantId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Email:ClientId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Email__ClientId", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Email:ClientSecret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Email__ClientSecret", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each required key on its own, so the guard is proven to name THE key that is missing rather
    /// than a fixed list that happens to contain it. Every one of these is an install that would
    /// start, claim mail, and silently deliver nothing.
    ///
    /// <para><c>Email:MailboxAddress</c> is in this set although it is not a credential: the system
    /// send path is <c>/users/{MailboxAddress}/sendMail</c>, so a blank value composes a Graph
    /// request for no mailbox at all. It is required by BOTH flows, which is why it is checked here
    /// rather than in <see cref="EmailOptions.MissingCredentialKeys"/>.</para>
    /// </summary>
    [Theory]
    [InlineData("Email:MailboxAddress")]
    [InlineData("Email:TenantId")]
    [InlineData("Email:ClientId")]
    [InlineData("Email:ClientSecret")]
    public void EachRequiredKey_RefusesOnItsOwn_AndTheRefusalNamesThatKey(string key)
    {
        var options = Blank(Complete(), key);

        var missing = EmailConfigurationGuard.MissingRequiredKeys(options);
        Assert.Equal(new[] { key }, missing.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => EmailConfigurationGuard.Validate(options));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains(EmailConfigurationGuard.EnvironmentKey(key), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whitespace is unset. A Helm value templated from an empty secret arrives as <c>" "</c>, and
    /// Azure's own validator rejects it exactly like <c>""</c> — so a guard that accepted it would
    /// wave through the very install it exists to catch.
    /// </summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n  ")]
    public void AWhitespaceValueIsUnset(string blank)
    {
        Assert.Equal(
            new[] { "Email:TenantId" },
            EmailConfigurationGuard.MissingRequiredKeys(Complete() with { TenantId = blank }).ToArray());
    }

    /// <summary>
    /// Enabled and nothing else: every required key of the selected (default, client-secret) flow
    /// is named at once, so one restart tells the operator the whole job rather than one key per
    /// roll.
    /// </summary>
    [Fact]
    public void EnabledWithNothingElse_NamesEveryRequiredKeyAtOnce()
    {
        var missing = EmailConfigurationGuard.MissingRequiredKeys(new EmailOptions { Enabled = true });

        Assert.Equal(
            new[] { "Email:MailboxAddress", "Email:TenantId", "Email:ClientId", "Email:ClientSecret" },
            missing.ToArray());
    }

    /// <summary>
    /// Managed identity carries no credential keys — <c>DefaultAzureCredential</c> takes no tenant
    /// id — but it still sends AS a mailbox. So a managed-identity install missing only the mailbox
    /// is refused for that key ALONE: naming tenant/client/secret here would send an operator to
    /// set three keys their flow does not use, which is a confidently wrong answer.
    /// </summary>
    [Fact]
    public void ManagedIdentity_IsRefusedOnlyForTheMailbox_NeverForCredentialKeysItDoesNotUse()
    {
        var options = new EmailOptions { Enabled = true, UseManagedIdentity = true };

        Assert.Equal(
            new[] { "Email:MailboxAddress" },
            EmailConfigurationGuard.MissingRequiredKeys(options).ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => EmailConfigurationGuard.Validate(options));
        Assert.DoesNotContain("Email:TenantId", ex.Message, StringComparison.Ordinal);
    }

    // ───────────── rule 3: enabled + complete starts ─────────────

    /// <summary>
    /// The control that stops every case above from being satisfied by a guard that refuses
    /// everything: a completely configured install passes, on BOTH flows. Managed identity is the
    /// production deployment shape — a false refusal here would stop mail on every install that
    /// works today.
    /// </summary>
    [Fact]
    public void ACompleteConfiguration_Starts_OnEitherCredentialFlow()
    {
        EmailConfigurationGuard.Validate(Complete());
        Assert.Empty(EmailConfigurationGuard.MissingRequiredKeys(Complete()));

        var managedIdentity = new EmailOptions
        {
            Enabled = true,
            UseManagedIdentity = true,
            MailboxAddress = "memex@example.test",
        };
        EmailConfigurationGuard.Validate(managedIdentity);
        Assert.Empty(EmailConfigurationGuard.MissingRequiredKeys(managedIdentity));
    }

    /// <summary>
    /// …and through the bound configuration, written the way an operator writes it: as
    /// <c>Email__*</c> environment entries. This is the shape the fix is asking a maintainer to
    /// produce, so it is worth asserting that it is actually accepted.
    /// </summary>
    [Fact]
    public void ACompleteConfigurationInEnvironmentVariableForm_PassesTheBootGuard()
    {
        EmailConfigurationGuard.Validate(Configuration(new Dictionary<string, string?>
        {
            ["Email__Enabled"] = "true",
            ["Email__MailboxAddress"] = "memex@example.test",
            ["Email__TenantId"] = "72f988bf-86f1-41af-91ab-2d7cd011db47",
            ["Email__ClientId"] = "client",
            ["Email__ClientSecret"] = "secret",
        }));
    }

    // ───────────── the message, and the one definition of "what does this flow need" ─────────────

    /// <summary>
    /// A refusal that only says what is wrong is half a message. This one has to carry every way
    /// OUT: complete the client-secret flow, switch to managed identity, or turn the integration
    /// off — the last of which matters most, because an operator who does not want mail on this
    /// install must not have to guess that <c>Email:Enabled=false</c> is a supported answer.
    /// </summary>
    [Fact]
    public void TheRefusalNamesEveryWayOut()
    {
        var message = EmailConfigurationGuard.Refusal(EmailConfigurationGuard.MissingRequiredKeys(TheIncident()));

        Assert.Contains("Email:UseManagedIdentity=true", message, StringComparison.Ordinal);
        Assert.Contains("Email__UseManagedIdentity=true", message, StringComparison.Ordinal);
        Assert.Contains("Email:Enabled=false", message, StringComparison.Ordinal);
        Assert.Contains("Email__Enabled=false", message, StringComparison.Ordinal);
        Assert.Contains("#2636", message, StringComparison.Ordinal);
    }

    /// <summary>The binder → environment mapping, pinned: every level separator becomes <c>__</c>.</summary>
    [Theory]
    [InlineData("Email:TenantId", "Email__TenantId")]
    [InlineData("Email:Enabled", "Email__Enabled")]
    [InlineData("Email:MailboxAddress", "Email__MailboxAddress")]
    public void TheEnvironmentFormOfAKey(string binder, string environment)
        => Assert.Equal(environment, EmailConfigurationGuard.EnvironmentKey(binder));

    /// <summary>
    /// 🚨 ONE definition of "which credential keys does the selected flow need". This guard
    /// delegates that half to <see cref="EmailOptions.MissingCredentialKeys"/> — the same answer
    /// <see cref="EmailDeliveryGuard"/> and <c>NoOpEmailSender</c> report — instead of re-deriving
    /// it. A second copy would be free to drift, and the two would then tell the same operator
    /// different stories about the same install.
    /// </summary>
    [Theory]
    [InlineData(false, "", "", "")]
    [InlineData(false, "t", "", "s")]
    [InlineData(false, "t", "c", "s")]
    [InlineData(true, "", "", "")]
    public void TheCredentialHalfIsTheSameAnswerTheWatchersGive(
        bool useManagedIdentity, string tenantId, string clientId, string clientSecret)
    {
        var options = new EmailOptions
        {
            Enabled = true,
            MailboxAddress = "memex@example.test",
            UseManagedIdentity = useManagedIdentity,
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = clientSecret,
        };

        Assert.Equal(
            EmailDeliveryGuard.MissingConfiguration(options).ToArray(),
            EmailConfigurationGuard.MissingRequiredKeys(options).ToArray());
    }

    // ───────────── the guard is WIRED, not merely present ─────────────

    /// <summary>
    /// 🚨 The case that stops all of the above from being dead code: the guard has to run on the
    /// portal boot path. <c>ConfigureMemexMesh</c> is what both hosts (Monolith and Distributed,
    /// plugins repo) call, and it is where <c>ValidateContentStorageDurability</c> and
    /// <c>MicrosoftTenant.Validate</c> already sit.
    ///
    /// <para>A pure function nobody calls refuses nothing, and its unit tests stay green forever —
    /// which is exactly how this class of guard rots.</para>
    /// </summary>
    [Fact]
    public void TheBootPathRefusesAHalfConfiguredEmailSection()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigureMemexMeshWith(new Dictionary<string, string?>
            {
                ["Email:Enabled"] = "true",
                ["Email:MailboxAddress"] = "memex@example.test",
                ["Email:ClientSecret"] = "secret",
            }));

        Assert.Contains("Email__TenantId", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and the boot path is unaffected by an install that never configured mail. Same call, no
    /// <c>Email</c> section at all — the #2510 direction, asserted where it would actually happen.
    /// </summary>
    [Fact]
    public void TheBootPathIsUnaffectedByAnInstallThatNeverConfiguredMail()
        => ConfigureMemexMeshWith(new Dictionary<string, string?>());

    // ───────────── fixtures ─────────────

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Runs the real portal boot configuration over an in-memory configuration, with the
    /// minimum storage answer that gets past the awaiting-setup short-circuit.</summary>
    private static void ConfigureMemexMeshWith(Dictionary<string, string?> settings)
    {
        var temp = Directory.CreateTempSubdirectory("mw-2636-").FullName;
        try
        {
            settings["Graph:Storage:Type"] = "FileSystem";
            settings["Graph:Storage:BasePath"] = temp;
            settings["Modules:Root"] = temp;

            new MeshBuilder(configure => configure(new ServiceCollection()), new Address("mesh", "test"))
                .ConfigureMemexMesh(Configuration(settings));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }

    /// <summary>The complete section with exactly one key blanked — "this deployment set everything
    /// but that one".</summary>
    private static EmailOptions Blank(EmailOptions options, string key) => key switch
    {
        "Email:MailboxAddress" => options with { MailboxAddress = "" },
        "Email:TenantId" => options with { TenantId = "" },
        "Email:ClientId" => options with { ClientId = "" },
        "Email:ClientSecret" => options with { ClientSecret = "" },
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a required Email key"),
    };
}
