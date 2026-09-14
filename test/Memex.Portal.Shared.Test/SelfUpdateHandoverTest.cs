#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;
using Route = Memex.Portal.Shared.SelfUpdate.SelfUpdateHandover.Route;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The PURE rules of the control-lane hand-over (MeshWeaver#4098): who applies, which route, what
/// is missing, what the event looks like and how it is signed — pinned without a mesh, so the
/// decision the poller takes on the fleet is the decision written here.
/// </summary>
public class SelfUpdateHandoverTest
{
    private const string Deployment = "build";
    private const string InboxUrl = "https://memex.systemorph.com/api/hooks/Hosting/PlatformBuilds";

    // ── who applies ─────────────────────────────────────────────────────────

    /// <summary>The chart AND the updater must both allow a self-patch; either alone is not a right.</summary>
    [Theory]
    [InlineData(true, true, Route.Post, SelfUpdateApply.SelfPatch)]
    [InlineData(true, true, Route.None, SelfUpdateApply.SelfPatch)]
    [InlineData(false, true, Route.Post, SelfUpdateApply.ControlLane)]
    [InlineData(true, false, Route.Post, SelfUpdateApply.ControlLane)]
    [InlineData(false, false, Route.Local, SelfUpdateApply.ControlLane)]
    [InlineData(false, true, Route.None, SelfUpdateApply.DetectOnly)]
    [InlineData(true, false, Route.None, SelfUpdateApply.DetectOnly)]
    public void WhoApplies_IsTheChartAndTheUpdaterTogether_ElseTheControlLane_ElseNobody(
        bool chartCanPatch, bool updaterCanPatch, Route route, SelfUpdateApply expected) =>
        SelfUpdateHandover.ApplyModeFor(chartCanPatch, updaterCanPatch, route).Should().Be(expected);

    // ── which route ─────────────────────────────────────────────────────────

    [Fact]
    public void ARecordIdIsRequiredOnEveryRoute()
    {
        var post = new SelfUpdateHandover.Settings("", InboxUrl, SecretPresent: true, LocalTargetListed: true, LocalSecretPresent: true, null);
        SelfUpdateHandover.RouteFor(post).Should().Be(Route.None,
            "an event naming no record on the control instance is not a hand-over — the control plane routes by it");
        SelfUpdateHandover.Missing(post).Should().Contain(SelfUpdateHandover.DeploymentKey);
    }

    [Fact]
    public void APostNeedsTheUrlAndTheSecret_AndIsPreferredOverLocal()
    {
        SelfUpdateHandover.RouteFor(new(Deployment, InboxUrl, true, false, false, null)).Should().Be(Route.Post);
        SelfUpdateHandover.RouteFor(new(Deployment, InboxUrl, true, true, true, null)).Should().Be(Route.Post,
            "an instance that declares a control inbox is a consumer, whatever it also lists");
        SelfUpdateHandover.RouteFor(new(Deployment, InboxUrl, false, false, false, null)).Should().Be(Route.None);
        SelfUpdateHandover.RouteFor(new(Deployment, null, true, false, false, null)).Should().Be(Route.None);
    }

    [Fact]
    public void TheControlInstanceDeliversLocally_OnlyWithItsTargetListedDeclaringAKeyWhoseSecretIsPresent()
    {
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, true, true, null) { LocalSecretKey = "Hosting:PlatformWebhookSecret" })
            .Should().Be(Route.Local);
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, true, false, null) { LocalSecretKey = "Hosting:PlatformWebhookSecret" })
            .Should().Be(Route.None);
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, false, true, null) { LocalSecretKey = "Hosting:PlatformWebhookSecret" })
            .Should().Be(Route.None);
        // 🚨 The route itself requires the DECLARED key: settings assembled by hand as "listed and
        // present" with no key must not reach local delivery either.
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, true, true, null)).Should().Be(Route.None,
            "a listed target without a declared SecretConfigKey is an unsigned target — the route, not only the reader, refuses it");
    }

    /// <summary>The missing sentence names the KEY that was actually read — never a value, and never
    /// a key the operator did not use: a URL derived from ReportTo is blamed on ReportTo, a listed
    /// local target on its own declared secret key. Null once a route exists.</summary>
    [Fact]
    public void WhatIsMissing_NamesTheKeyThatWasRead()
    {
        SelfUpdateHandover.Missing(new(Deployment, InboxUrl, true, false, false, null)).Should().BeNull();
        SelfUpdateHandover.Missing(new(Deployment, null, false, false, false, null))
            .Should().Contain(SelfUpdateHandover.UrlKey).And.Contain(SelfUpdateHandover.ReportToKey);
        SelfUpdateHandover.Missing(new(Deployment, InboxUrl, false, false, false, null) { UrlFrom = SelfUpdateHandover.UrlSource.Declared })
            .Should().Contain(SelfUpdateHandover.UrlKey).And.Contain(SelfUpdateHandover.SecretKey)
            .And.NotContain(InboxUrl.Substring(8, 5), "no value is repeated, only keys");
        SelfUpdateHandover.Missing(new(Deployment, InboxUrl, false, false, false, null) { UrlFrom = SelfUpdateHandover.UrlSource.Derived })
            .Should().Contain(SelfUpdateHandover.ReportToKey).And.Contain(SelfUpdateHandover.SecretKey)
            .And.NotContain(SelfUpdateHandover.UrlKey, "the operator never set the declared URL key — blaming it sends them to the wrong line");
        SelfUpdateHandover.Missing(new(Deployment, null, false, true, false, null) { LocalSecretKey = "Hosting:PlatformWebhookSecret" })
            .Should().Contain("Hosting:PlatformWebhookSecret").And.Contain(SelfUpdateHandover.InboxTarget)
            .And.NotContain(SelfUpdateHandover.UrlKey, "a listed local target is the control instance's shape; the URL keys are not what is missing");
        SelfUpdateHandover.Missing(new(Deployment, null, false, true, false, null))
            .Should().Contain(WebhookInbox.SecretConfigKeyName).And.Contain("unverified",
                "a listed target that declares no secret key would store the event unverified — that is the defect to name");
    }

    // ── the URL ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheInboxUrl_IsDeclared_OrDerivedFromTheControlInstance()
    {
        SelfUpdateHandover.ResolveUrl(InboxUrl, "https://other.example").Should().Be(InboxUrl, "a declared URL wins");
        SelfUpdateHandover.ResolveUrl(null, "https://memex.systemorph.com/").Should().Be(InboxUrl,
            "the control instance the inventory report goes to serves the same inbox");
        SelfUpdateHandover.ResolveUrl("", "").Should().BeNull();
    }

    /// <summary>🚨 A value carrying userinfo, or that is not http(s), declares no inbox rather than half of one.</summary>
    [Fact]
    public void AUrlThatCannotBeReadWhole_DeclaresNoInbox()
    {
        SelfUpdateHandover.ResolveUrl("https://memex.systemorph.com@evil.example/api/hooks/x", null).Should().BeNull();
        SelfUpdateHandover.ResolveUrl(null, "memex.systemorph.com").Should().BeNull("a bare host is not an absolute URL here");
        SelfUpdateHandover.ResolveUrl("mailto:ops@example.test", null).Should().BeNull();
    }

    // ── the event ───────────────────────────────────────────────────────────

    [Fact]
    public void TheBody_IsTheInboxContract_EventFirst_CamelCase_NullsOmitted()
    {
        var body = SelfUpdateHandover.Body(new SelfUpdateHandover.Announcement
        {
            Deployment = Deployment,
            CurrentVersion = "3.0.0-ci.8411",
            NewVersion = "3.0.0-ci.8460",
            CurrentImage = "cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411",
            NewImage = "cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8460",
            Policy = "Continuous",
            Pattern = "3.0.0-ci*",
            Trigger = "SafetyNet",
            DetectedAt = "2026-09-14T08:00:00Z",
        });

        body.Should().StartWith("{\"event\":\"self-update-available\"", "the router keys on the first field");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("deployment").GetString().Should().Be(Deployment);
        root.GetProperty("newImage").GetString().Should().EndWith(":3.0.0-ci.8460");
        root.GetProperty("policy").GetString().Should().Be("Continuous");
        root.GetProperty("reporter").GetString().Should().Be(SelfUpdateHandover.Reporter);
        root.TryGetProperty("reason", out _).Should().BeFalse("a release announcement carries no restart reason");
        root.TryGetProperty("instance", out _).Should().BeFalse("nulls are omitted, not written");
    }

    [Fact]
    public void ARestartAnnouncement_NamesNoNewImage()
    {
        var body = SelfUpdateHandover.Body(new SelfUpdateHandover.Announcement
        {
            Event = SelfUpdateHandover.RestartEvent,
            Deployment = Deployment,
            CurrentVersion = "3.0.0-ci.8411",
            CurrentImage = "cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411",
            Reason = "a landed module generation is pending activation",
            DetectedAt = "2026-09-14T08:00:00Z",
        });
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("event").GetString().Should().Be("self-update-restart-pending");
        doc.RootElement.TryGetProperty("newImage", out _).Should().BeFalse();
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("pending activation");
    }

    /// <summary>The signature is the inventory report's — GitHub's shape — and verifies with the inbox's own verifier.</summary>
    [Fact]
    public void TheSignature_IsGitHubsShape_AndTheInboxVerifiesIt()
    {
        SelfUpdateHandover.Sign("The quick brown fox jumps over the lazy dog", "key")
            .Should().Be("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");
        var body = SelfUpdateHandover.Body(new SelfUpdateHandover.Announcement { Deployment = Deployment });
        WebhookInbox.VerifyHmacSha256(SelfUpdateHandover.Sign(body, "s3cret"), body, "s3cret").Should().BeTrue();
        WebhookInbox.VerifyHmacSha256(SelfUpdateHandover.Sign(body, "s3cret"), body, "other").Should().BeFalse();
    }

    // ── reading the settings ───────────────────────────────────────────────

    /// <summary>Settings carry PRESENCE of a secret, never the secret — a loggable record.</summary>
    [Fact]
    public void TheSettings_CarryPresenceOfTheSecret_NeverItsValue()
    {
        const string secret = "mwi_never_printed";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SelfUpdateHandover.DeploymentKey] = Deployment,
            [SelfUpdateHandover.ReportToKey] = "https://memex.systemorph.com",
            [SelfUpdateHandover.SecretKey] = secret,
            ["PluginCatalog:HomeUrl"] = "https://build.meshweaver.cloud",
        }).Build();

        var settings = SelfUpdateHandover.ReadSettings(config);

        settings.Should().Be(new SelfUpdateHandover.Settings(
            Deployment, InboxUrl, SecretPresent: true, LocalTargetListed: false, LocalSecretPresent: false,
            "https://build.meshweaver.cloud") { UrlFrom = SelfUpdateHandover.UrlSource.Derived });
        settings.ToString().Should().NotContain(secret, "the record is what a log line would print");
        SelfUpdateHandover.RouteFor(settings).Should().Be(Route.Post);
    }

    /// <summary>The control instance: the target listed under WebhookInbox:Targets with its own secret key present.</summary>
    [Fact]
    public void TheControlInstance_ReadsAsLocal()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SelfUpdateHandover.DeploymentKey] = "memex",
            ["WebhookInbox:Targets:0"] = SelfUpdateHandover.InboxTarget,
            ["WebhookInbox:Targets:0:SecretConfigKey"] = SelfUpdateHandover.LocalSecretKey,
            [SelfUpdateHandover.LocalSecretKey] = "fleet-secret",
        }).Build();

        var settings = SelfUpdateHandover.ReadSettings(config);

        settings.LocalTargetListed.Should().BeTrue();
        settings.LocalSecretKey.Should().Be(SelfUpdateHandover.LocalSecretKey, "the key the target DECLARES, read back for the diagnostic");
        settings.LocalSecretPresent.Should().BeTrue();
        settings.Url.Should().BeNull();
        SelfUpdateHandover.RouteFor(settings).Should().Be(Route.Local);
    }

    /// <summary>🚨 A listed target that declares NO SecretConfigKey is an unsigned target by the inbox's
    /// contract (#3312) — never "the default key". The local route must not be selected for it, or the
    /// event is stored with its signature never checked.</summary>
    [Fact]
    public void ABareLocalTarget_IsNotALocalRoute()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SelfUpdateHandover.DeploymentKey] = "memex",
            ["WebhookInbox:Targets:0"] = SelfUpdateHandover.InboxTarget,
            [SelfUpdateHandover.LocalSecretKey] = "fleet-secret",
        }).Build();

        var settings = SelfUpdateHandover.ReadSettings(config);

        settings.LocalTargetListed.Should().BeTrue();
        settings.LocalSecretKey.Should().BeNull();
        settings.LocalSecretPresent.Should().BeFalse("a secret that happens to exist under the fleet's usual name is not one the target declares");
        SelfUpdateHandover.RouteFor(settings).Should().Be(Route.None);
        SelfUpdateHandover.Missing(settings).Should().Contain(WebhookInbox.SecretConfigKeyName);
    }

    /// <summary>A URL derived from ReportTo is recorded as derived — the diagnostic reads it.</summary>
    [Fact]
    public void ADerivedUrl_IsRecordedAsDerived()
    {
        SelfUpdateHandover.ResolveUrlAndSource(null, "https://memex.systemorph.com").Source
            .Should().Be(SelfUpdateHandover.UrlSource.Derived);
        SelfUpdateHandover.ResolveUrlAndSource(InboxUrl, null).Source
            .Should().Be(SelfUpdateHandover.UrlSource.Declared);
        SelfUpdateHandover.ResolveUrlAndSource("", "").Source
            .Should().Be(SelfUpdateHandover.UrlSource.None);
    }

    /// <summary>🚨 Only an inbox answer that says the delivery was ACCEPTED and the signature VERIFIED
    /// is a hand-over. "not-required" (the target declares no secret key, so nothing was checked), a
    /// status other than accepted, and a body that is not the inbox contract are refusals — the sender
    /// must not record a delivery the receiver may drop.</summary>
    [Fact]
    public void OnlyAnAcceptedVerifiedAnswerIsAHandover()
    {
        SelfUpdateHandover.InboxAnswerOf("{\"status\":\"accepted\",\"signature\":\"verified\"}").Should().Be(("accepted", "verified"));
        SelfUpdateHandover.InboxAnswerOf("{\"status\":\"accepted\",\"signature\":\"not-required\"}").Should().Be(("accepted", "not-required"));
        SelfUpdateHandover.InboxAnswerOf("{\"status\":\"rejected\",\"signature\":\"verified\"}").Should().Be(("rejected", "verified"),
            "a verified signature on a delivery the inbox did not accept is not a delivery — the caller requires both halves");
        SelfUpdateHandover.InboxAnswerOf("{\"signature\":\"verified\"}").Should().Be(((string?)null, (string?)"verified"));
        SelfUpdateHandover.InboxAnswerOf("").Should().Be(((string?)null, (string?)null));
        SelfUpdateHandover.InboxAnswerOf("OK").Should().Be(((string?)null, (string?)null));
        SelfUpdateHandover.InboxAnswerOf("{\"status\":\"accepted\"}").Should().Be(((string?)"accepted", (string?)null));
    }

    [Fact]
    public void NothingConfigured_ReadsAsNoRoute_NamingTheRecordIdFirst()
    {
        var settings = SelfUpdateHandover.ReadSettings(new ConfigurationBuilder().Build());
        SelfUpdateHandover.RouteFor(settings).Should().Be(Route.None);
        SelfUpdateHandover.Missing(settings).Should().Contain(SelfUpdateHandover.DeploymentKey);
    }

    /// <summary>The restart hand-over and the release hand-over are pinned as outcomes a check can reach.</summary>
    [Fact]
    public void TheHandedOverVerdicts_AreNewerReleaseFindings_AndTakeNoRestart()
    {
        var handed = SelfUpdateVerdict.HandedOver("3.0.0-ci.9", InboxUrl, "accepted (200)");
        handed.FoundNewerRelease.Should().BeTrue("the dead-event-channel report must still fire on a handed-over release");
        SelfUpdateVerdict.MayRestartAfter(handed).Should().BeFalse("the Roll the control plane opens restarts the pods");
        var failed = SelfUpdateVerdict.HandoverFailed("3.0.0-ci.9", "401 Unauthorized");
        failed.FoundNewerRelease.Should().BeTrue();
        failed.Message.Should().Contain("FAILED").And.Contain("401");
        SelfUpdateVerdict.MayRestartAfter(failed).Should().BeTrue(
            "a hand-over that failed handed nothing to anyone — a pending module restart is still considered, never skipped");
        var detect = SelfUpdateVerdict.DetectOnly("3.0.0-ci.9", "no control inbox: Hosting:Deployment is not set");
        detect.Outcome.Should().Be(SelfUpdateOutcome.DetectOnly);
        detect.Message.Should().Contain("Hosting:Deployment");
    }

    // ── the Updates tab ────────────────────────────────────────────────────

    private static string Echo(string key, object?[] args) => key + "[" + string.Join(",", args) + "]";

    /// <summary>🚨 A release handed to the control instance must not read as "update available" for
    /// ever on the Updates tab: the durable HandedOver* fields render as WHERE and WHEN it went, in
    /// the viewer's zone, for the tag the tab is about.</summary>
    [Fact]
    public void TheUpdatesTab_SaysWhereAHandedOverReleaseWent()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = "3.0.0-ci.9",
            CheckedAt = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero),
            HandedOverTag = "3.0.0-ci.9",
            HandedOverAt = new DateTimeOffset(2026, 9, 14, 8, 1, 0, TimeSpan.Zero),
            HandedOverTo = InboxUrl,
        };

        var markdown = Memex.Portal.Shared.Settings.UpdatePolicySettingsTab.StatusMarkdown(content, Echo, "Europe/Zurich");

        markdown.Should().Contain("ui.updateLatestAvailable[3.0.0-ci.9]");
        markdown.Should().Contain($"ui.updateHandedOverLine[3.0.0-ci.9,{InboxUrl},2026-09-14 10:01]",
            "the tag, the destination and the instant in the viewer's zone");
    }

    /// <summary>A hand-over of an OLDER tag is history: the current tag renders without the line.</summary>
    [Fact]
    public void TheUpdatesTab_DoesNotAttributeAnOldHandoverToANewTag()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = "3.0.0-ci.10",
            HandedOverTag = "3.0.0-ci.9",
            HandedOverAt = DateTimeOffset.UtcNow,
            HandedOverTo = InboxUrl,
        };

        var markdown = Memex.Portal.Shared.Settings.UpdatePolicySettingsTab.StatusMarkdown(content, Echo);

        markdown.Should().Contain("ui.updateLatestAvailable[3.0.0-ci.10]");
        markdown.Should().NotContain("ui.updateHandedOverLine");
    }
}
