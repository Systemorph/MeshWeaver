#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A local portal must be signable-into without a cloud identity provider.</b>
///
/// <para><c>values.local.yaml</c> is the overlay <c>memex-local</c> seeds a fresh machine from, so
/// whatever it defaults to IS the local developer experience. It used to default to Microsoft Entra
/// and offer DevLogin as a commented fallback — meaning signing in to your own laptop required a
/// tenant, a dedicated app registration, two registered redirect URIs, and a real client secret
/// written to disk. DevLogin needs none of that: <c>DevAuthController</c> self-provisions the user
/// on first sign-in.</para>
///
/// <para><b>The pair rule.</b> <c>EnableDevLogin</c> turns the login on; <c>DevAdminUsers</c> is who
/// gets platform admin through it. Shipping one without the other yields a portal you can log into
/// and then administer nothing on — the same defect <c>DevLoginConfigBindingTest</c> pins on the
/// configmap side, here on the side that seeds the value in the first place.</para>
///
/// <para>Entra stays SUPPORTED and opt-in: exercising the OAuth flow locally is legitimate, and a
/// real defect in exactly that path (a provider list written into the wrong options type) reached
/// production. What this pins is the DEFAULT, not the capability.</para>
/// </summary>
public class LocalOverlayDefaultsToDevLoginTest
{
    private static string Overlay()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "deploy", "homebrew", "share", "values.local.yaml"));
    }

    /// <summary>Lines that are actually SET — a key named only in a comment configures nothing.</summary>
    private static bool IsSet(string key) => Overlay()
        .Split('\n')
        .Select(l => l.Trim())
        .Any(l => !l.StartsWith('#') && l.StartsWith(key + ":", StringComparison.Ordinal));

    [Fact]
    public void DevLoginIsOnByDefault()
    {
        Assert.True(IsSet("Authentication__EnableDevLogin"),
            "a fresh local install must be able to sign in without a cloud identity provider");
    }

    /// <summary>
    /// The other half of the pair. Without it the developer signs in successfully and then cannot
    /// administer the portal they just created — which reads as a permissions bug, not a missing
    /// setting.
    /// </summary>
    [Fact]
    public void DevAdminUsersIsSetAlongsideIt()
    {
        Assert.True(IsSet("Authentication__DevAdminUsers"),
            "EnableDevLogin and DevAdminUsers are a pair — set both, or neither");
    }

    /// <summary>
    /// Entra must be OPT-IN. A set ClientId makes HasExternalProviders true, which puts the
    /// Microsoft button on the login page and moves the sign-out path to /auth/logout — the whole
    /// cloud-dependency this default exists to avoid.
    /// </summary>
    [Theory]
    [InlineData("Authentication__Provider")]
    [InlineData("Authentication__Microsoft__ClientId")]
    [InlineData("Authentication__Microsoft__TenantId")]
    [InlineData("Authentication__Microsoft__ClientSecret")]
    public void EntraIsCommentedOut(string key)
    {
        Assert.False(IsSet(key),
            $"{key} must stay commented — local sign-in may not require an Entra tenant");
    }

    /// <summary>
    /// …but it must still be DOCUMENTED, or opting in becomes guesswork. Deleting the lines
    /// outright would pass every assertion above while removing a capability that is legitimately
    /// needed to test the OAuth flow locally.
    /// </summary>
    [Fact]
    public void EntraRemainsDocumentedAsAnOptIn()
    {
        var text = Overlay();
        Assert.Contains("Authentication__Microsoft__ClientId", text, StringComparison.Ordinal);
        Assert.Contains("signin-microsoft", text, StringComparison.Ordinal);
    }
}
