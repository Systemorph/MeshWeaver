#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The local overlay must ASK for the identity and storage decisions, not make them.</b>
///
/// <para><c>values.local.yaml</c> is the overlay <c>memex-local</c> seeds a fresh machine from, so
/// whatever it states IS the local install's configuration. It has now stopped stating two things
/// on the operator's behalf: which database, and how people sign in. The portal's first-run wizard
/// asks both (<c>memex-local setup</c>), and the overlay leaves the keys blank so the instance
/// manifest the wizard writes is what answers them.</para>
///
/// <para><b>This guard replaces <c>LocalOverlayDefaultsToDevLoginTest</c>, and it is the same
/// concern moved rather than dropped.</b> That test existed because signing in to your own laptop
/// once required an Entra tenant, an app registration, two redirect URIs and a real client secret on
/// disk; it pinned the overlay's default to DevLogin so it could not regress. The property still
/// matters and is still pinned — it has moved to where the default now lives, the wizard itself
/// (<c>SetupSurfaceTest.TheDeveloperLogin_IsOnByDefault</c>, which asserts the checkbox renders
/// checked). What this file pins is the other half: that the overlay does not quietly answer the
/// question first, because a deployment value OUTRANKS the manifest, so a stated
/// <c>Authentication__EnableDevLogin</c> would silently discard whatever the operator chose.</para>
///
/// <para>Entra stays SUPPORTED — the wizard offers it, and pinning it in this file is still the way
/// an unattended install skips the wizard. What may not happen is this file deciding for you.</para>
/// </summary>
public class LocalOverlayAsksInsteadOfAnsweringTest
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

    /// <summary>
    /// The value a key is SET to, or null when it is absent or only named in a comment.
    ///
    /// <para>A key named in a comment configures nothing, and a key set to <c>""</c> configures
    /// nothing either — the chart emits these only when they carry a value. The distinction between
    /// the two matters here: <c>""</c> is a deliberate, documented "the wizard asks this", while
    /// absence is indistinguishable from someone having deleted the line.</para>
    /// </summary>
    private static string? ValueOf(string key) => Overlay()
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => !l.StartsWith('#') && l.StartsWith(key + ":", StringComparison.Ordinal))
        .Select(l => l[(key.Length + 1)..].Trim().Trim('"'))
        .FirstOrDefault();

    /// <summary>
    /// 🚨 The sign-in question belongs to the wizard. A stated value here WINS over the instance
    /// manifest — deployment configuration always outranks it — so an overlay that answered would
    /// discard the operator's choice without a word, and a freshly set-up instance could end up
    /// with no way in.
    /// </summary>
    [Theory]
    [InlineData("Authentication__EnableDevLogin")]
    [InlineData("Authentication__DevAdminUsers")]
    [InlineData("Authentication__Provider")]
    [InlineData("Authentication__Microsoft__ClientId")]
    [InlineData("Authentication__Microsoft__TenantId")]
    [InlineData("Authentication__Microsoft__ClientSecret")]
    public void TheOverlayDoesNotAnswerTheSignInQuestion(string key)
    {
        var value = ValueOf(key);
        Assert.True(string.IsNullOrEmpty(value),
            $"{key} is set to '{value}' — the first-run wizard asks this, and a value here silently "
            + "overrides the operator's answer. Blank it, or pin it deliberately for an unattended "
            + "install and update this guard to say so.");
    }

    /// <summary>
    /// 🚨 And the database question, for a sharper reason: an EMPTY <c>Graph:Storage</c> section is
    /// not an ABSENT one. <c>GraphStorageConfig.Type</c> defaults to <c>FileSystem</c>, so a stated
    /// blank would bind to a working-looking file-system store on container-ephemeral disk — the
    /// instance boots straight past setup and loses everything on the next roll.
    /// </summary>
    [Theory]
    [InlineData("Graph__Storage__Type")]
    [InlineData("Graph__Storage__BasePath")]
    public void TheOverlayDoesNotAnswerTheDatabaseQuestion(string key)
    {
        var value = ValueOf(key);
        Assert.True(string.IsNullOrEmpty(value),
            $"{key} is set to '{value}' — a fresh local install must reach the setup wizard, which "
            + "it can only do with no storage configured.");
    }

    /// <summary>
    /// The keys must still be PRESENT and blank rather than deleted. Blank is a documented "the
    /// wizard asks this"; a deleted line is indistinguishable from an accident, and leaves an
    /// operator wanting an unattended install with nothing to copy.
    /// </summary>
    [Theory]
    [InlineData("Authentication__EnableDevLogin")]
    [InlineData("Authentication__Microsoft__ClientId")]
    [InlineData("Graph__Storage__Type")]
    public void TheKeysAreBlankRatherThanDeleted(string key)
        => Assert.NotNull(ValueOf(key));

    /// <summary>
    /// Entra must stay DOCUMENTED. The wizard collects the client id, tenant and secret, but it
    /// cannot register the redirect URIs on the app registration for you — and an operator who does
    /// not know they are needed gets a sign-in that fails at the callback.
    /// </summary>
    [Fact]
    public void EntraRemainsDocumentedIncludingItsRedirectUris()
    {
        var text = Overlay();
        Assert.Contains("Authentication__Microsoft__ClientId", text, StringComparison.Ordinal);
        Assert.Contains("signin-microsoft", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlay must point at the wizard, or a fresh install ends with a portal that redirects
    /// everything to <c>/setup</c> and no indication of where the token comes from.
    /// </summary>
    [Fact]
    public void TheOverlayPointsAtTheWizard()
        => Assert.Contains("memex-local setup", Overlay(), StringComparison.Ordinal);
}
