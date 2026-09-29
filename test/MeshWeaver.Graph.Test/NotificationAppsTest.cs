using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pure tests for per-app notification settings (<see cref="NotificationApps"/>): which app a
/// notification belongs to, and what the person's app preference lets through — iOS
/// <i>Settings → Notifications → {app}</i>, with "Deliver quietly" as provisional authorization.
/// </summary>
public class NotificationAppsTest
{
    private static readonly IReadOnlyCollection<InstalledAppRef> Installed =
    [
        new("Chess", "Chess"),
        new("parties-app", "Parties"),
        new("Deep", "Tools/Deep"),
        new("Tools", "Tools"),
    ];

    private static readonly ImmutableHashSet<string> All =
        [NotificationChannelKind.InApp, NotificationChannelKind.Teams, NotificationChannelKind.Email];

    private const string ModuleFeature = "gameMove";

    [Theory]
    [InlineData("Chess/Game/1", "Chess")]
    [InlineData("Chess", "Chess")]
    [InlineData("Parties/SampleDossier", "parties-app")]
    [InlineData("alice/Parties/SampleDossier", "parties-app")]
    [InlineData("Tools/Deep/x", "Deep")]
    [InlineData("Tools/Other", "Tools")]
    [InlineData("Chessboard/x", null)]
    [InlineData("alice/Documents/spec", null)]
    [InlineData("Systemorph/Ops/roll", null)]
    public void Attribute_ByTheLongestInstalledPluginPrefix(string path, string? expected)
        => Assert.Equal(expected, NotificationApps.Attribute(null, null, path, "alice", Installed));

    [Fact]
    public void Attribute_TheTargetIsTriedBeforeTheMainNode()
        => Assert.Equal("Chess", NotificationApps.Attribute(null, "Chess/Game/1", "Parties/x", "alice", Installed));

    [Fact]
    public void Attribute_AnotherPersonsHome_IsNotTheirApp()
        => Assert.Null(NotificationApps.Attribute(null, null, "bob/Parties/SampleDossier", "alice", Installed));

    [Theory]
    [InlineData("Parties", "parties-app")]
    [InlineData("parties-app", "parties-app")]
    [InlineData("NotInstalled", "NotInstalled")]
    public void Attribute_AnExplicitAppWins(string app, string expected)
        => Assert.Equal(expected, NotificationApps.Attribute(app, null, "alice/whatever", "alice", Installed));

    [Fact]
    public void Gate_NoApp_ThePlatform_PassesTheFeatureChannels()
        => Assert.Equal(All, NotificationApps.Gate(ModuleFeature, All, null, null));

    [Fact]
    public void Gate_AnUnconfiguredApp_DeliversItsOwnNotificationsQuietly_BellOnly()
    {
        var gated = NotificationApps.Gate(ModuleFeature, All, "Chess",
            PreferenceRead<NotificationAppPreference>.Absent());
        Assert.Equal([NotificationChannelKind.InApp], gated);
    }

    [Theory]
    [InlineData(NotificationFeatures.Approvals)]
    [InlineData(NotificationFeatures.AccessGranted)]
    public void Gate_AnUnconfiguredApp_DoesNotQuietThePlatformsOwnKinds(string feature)
        => Assert.Equal(All, NotificationApps.Gate(feature, All, "Chess",
            PreferenceRead<NotificationAppPreference>.Absent()));

    [Theory]
    [InlineData(ModuleFeature)]
    [InlineData(NotificationFeatures.Approvals)]
    public void Gate_MasterSwitchOff_SilencesEverything(string feature)
        => Assert.Empty(NotificationApps.Gate(feature, All, "Chess",
            PreferenceRead<NotificationAppPreference>.Found(new() { AllowNotifications = false })));

    [Fact]
    public void Gate_QuietOff_TheAppReachesTheChannelsItIsAllowed()
    {
        var gated = NotificationApps.Gate(ModuleFeature, All, "Chess",
            PreferenceRead<NotificationAppPreference>.Found(new() { DeliverQuietly = false, Email = false }));
        Assert.Equal([NotificationChannelKind.InApp, NotificationChannelKind.Teams], gated);
    }

    [Fact]
    public void Gate_TheAppCanOnlyTakeAway_NeverAddAChannelTheFeatureSwitchedOff()
    {
        ImmutableHashSet<string> bellOnly = [NotificationChannelKind.InApp];
        var gated = NotificationApps.Gate(ModuleFeature, bellOnly, "Chess",
            PreferenceRead<NotificationAppPreference>.Found(new() { DeliverQuietly = false }));
        Assert.Equal(bellOnly, gated);
    }

    [Fact]
    public void Gate_AnUnreadableAppPreference_FailsClosed_BellOnly()
        => Assert.Equal([NotificationChannelKind.InApp], NotificationApps.Gate(NotificationFeatures.Approvals, All,
            "Chess", PreferenceRead<NotificationAppPreference>.Unreadable("timeout")));

    [Fact]
    public void Gate_AnUnreadableAppPreference_NeverAddsTheBell()
        => Assert.Empty(NotificationApps.Gate(NotificationFeatures.Approvals, [NotificationChannelKind.Teams],
            "Chess", PreferenceRead<NotificationAppPreference>.Unreadable("timeout")));

    [Theory]
    [InlineData("Chess", true)]
    [InlineData("parties-app", true)]
    [InlineData("a.b_c-1", true)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    public void AppKeys_AreOnePathSegment(string key, bool valid)
        => Assert.Equal(valid, NotificationApps.IsValidKey(key));

    [Fact]
    public void AppPreferences_LiveBesideTheFeaturePreferences_InTheirOwnSegment()
        => Assert.Equal("alice/_Settings/Notifications/Apps/Chess", NotificationAppPreferencePaths.PathFor("alice", "Chess"));
}
