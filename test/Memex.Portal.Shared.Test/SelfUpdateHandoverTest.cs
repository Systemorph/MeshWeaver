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
    public void TheControlInstanceDeliversLocally_OnlyWithItsTargetListedAndItsSecretPresent()
    {
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, true, true, null)).Should().Be(Route.Local);
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, true, false, null)).Should().Be(Route.None);
        SelfUpdateHandover.RouteFor(new(Deployment, null, false, false, true, null)).Should().Be(Route.None);
    }

    /// <summary>The missing sentence names KEYS, never values, and is null once a route exists.</summary>
    [Fact]
    public void WhatIsMissing_NamesTheKey()
    {
        SelfUpdateHandover.Missing(new(Deployment, InboxUrl, true, false, false, null)).Should().BeNull();
        SelfUpdateHandover.Missing(new(Deployment, null, false, false, false, null))
            .Should().Contain(SelfUpdateHandover.UrlKey).And.Contain(SelfUpdateHandover.ReportToKey);
        SelfUpdateHandover.Missing(new(Deployment, InboxUrl, false, false, false, null))
            .Should().Contain(SelfUpdateHandover.SecretKey).And.NotContain(InboxUrl.Substring(8, 5), "no value is repeated, only keys");
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
            "https://build.meshweaver.cloud"));
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
        settings.LocalSecretPresent.Should().BeTrue();
        settings.Url.Should().BeNull();
        SelfUpdateHandover.RouteFor(settings).Should().Be(Route.Local);
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
        var detect = SelfUpdateVerdict.DetectOnly("3.0.0-ci.9", "no control inbox: Hosting:Deployment is not set");
        detect.Outcome.Should().Be(SelfUpdateOutcome.DetectOnly);
        detect.Message.Should().Contain("Hosting:Deployment");
    }
}
