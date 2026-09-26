using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pure tests for the per-feature channel preference rule (<see cref="NotificationChannelPreferences.Resolve"/>)
/// and the feature vocabulary — no mesh needed. The rule: a person's own node for a feature is taken
/// as written; with none, every feature reaches the bell and Teams, and the bell/email of a feature
/// that had a legacy per-category row keep what that row says.
/// </summary>
public class NotificationChannelPreferencesTest
{
    public static TheoryData<string> EveryBuiltInFeatureAndAnUnknownOne()
    {
        var data = new TheoryData<string>();
        foreach (var d in NotificationFeatures.BuiltIn)
            data.Add(d.Feature);
        data.Add("a-module-feature-nobody-registered");
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryBuiltInFeatureAndAnUnknownOne))]
    public void Default_SetNothing_ReachesTheBellAndTeams_ForEveryFeature(string feature)
    {
        var effective = NotificationChannelPreferences.Resolve(feature, explicitPreference: null, legacy: null);

        Assert.Equal(feature, effective.Feature);
        Assert.True(effective.Bell, $"{feature}: the bell is on by default");
        Assert.True(effective.Teams, $"{feature}: Teams is on by default");
        Assert.Contains(NotificationChannelKind.InApp, effective.Channels());
        Assert.Contains(NotificationChannelKind.Teams, effective.Channels());
    }

    [Theory]
    [InlineData(NotificationFeatures.Approvals, true)]
    [InlineData(NotificationFeatures.AccessGranted, true)]
    [InlineData(NotificationFeatures.ChatReady, false)]
    [InlineData(NotificationFeatures.System, false)]
    [InlineData(NotificationFeatures.Inbox, false)]
    [InlineData(NotificationFeatures.Triage, false)]
    public void Default_Email_KeepsTheLegacyCategoryDefault_AndIsOffForNewFeatures(string feature, bool email)
        => Assert.Equal(email, NotificationChannelPreferences.Resolve(feature, null, null).Email);

    [Fact]
    public void Default_ALegacyBellSwitchedOff_IsRespected_AndTeamsStillDefaultsOn()
    {
        var legacy = new NotificationSettings { ChatReadyInApp = false, ApprovalsEmail = false };

        var chat = NotificationChannelPreferences.Resolve(NotificationFeatures.ChatReady, null, legacy);
        var approvals = NotificationChannelPreferences.Resolve(NotificationFeatures.Approvals, null, legacy);

        Assert.False(chat.Bell);
        Assert.True(chat.Teams);
        Assert.False(approvals.Email);
        Assert.True(approvals.Bell);
    }

    [Fact]
    public void PerFeatureOverride_IsTakenAsWritten_AndOverridesTheLegacyRow()
    {
        var own = new NotificationFeaturePreference { Bell = false, Teams = true, Email = true };
        // The legacy row says the opposite for every field — the person's own node wins.
        var legacy = new NotificationSettings { ApprovalsInApp = true, ApprovalsEmail = false };

        var effective = NotificationChannelPreferences.Resolve(NotificationFeatures.Approvals, own, legacy);

        Assert.Equal(NotificationFeatures.Approvals, effective.Feature);
        Assert.Equal(
            new[] { NotificationChannelKind.Email, NotificationChannelKind.Teams },
            effective.Channels().OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void PerFeatureOverride_OnlyChangesItsOwnFeature()
    {
        var approvalsOff = new NotificationFeaturePreference { Bell = true, Teams = false };

        Assert.False(NotificationChannelPreferences.Resolve(NotificationFeatures.Approvals, approvalsOff, null).Teams);
        // Another feature, with no node of its own, still gets the default.
        Assert.True(NotificationChannelPreferences.Resolve(NotificationFeatures.Inbox, null, null).Teams);
    }

    [Fact]
    public void AllChannelsOff_IsASilentFeature()
        => Assert.Empty(new NotificationFeaturePreference { Bell = false, Teams = false, Email = false }.Channels());

    [Theory]
    [InlineData(NotificationType.ApprovalRequired, NotificationFeatures.Approvals)]
    [InlineData(NotificationType.ApprovalGiven, NotificationFeatures.Approvals)]
    [InlineData(NotificationType.ApprovalRejected, NotificationFeatures.Approvals)]
    [InlineData(NotificationType.AccessGranted, NotificationFeatures.AccessGranted)]
    [InlineData(NotificationType.ChatReady, NotificationFeatures.ChatReady)]
    [InlineData(NotificationType.System, NotificationFeatures.System)]
    [InlineData(NotificationType.General, NotificationFeatures.System)]
    public void ANotificationWithoutAFeature_GetsTheOneItsTypeImplies(NotificationType type, string feature)
    {
        Assert.Equal(feature, type.ToFeature());
        Assert.Equal(feature, new Notification { NotificationType = type }.FeatureOf());
        Assert.Equal(feature, new NotificationRequest
        {
            MainNodePath = "x",
            Title = MeshWeaver.Data.LocalizableText.Verbatim("t"),
            Message = MeshWeaver.Data.LocalizableText.Verbatim("m"),
            Type = type,
        }.EffectiveFeature());
    }

    [Fact]
    public void AnExplicitFeature_WinsOverTheType()
        => Assert.Equal(NotificationFeatures.Triage,
            new Notification { NotificationType = NotificationType.System, Feature = NotificationFeatures.Triage }.FeatureOf());

    [Fact]
    public void ThePreferenceNode_LivesUnderThePersonsNotificationSettings()
    {
        Assert.Equal("alice/_Settings/Notifications/approvals",
            NotificationFeaturePreferencePaths.PathFor("alice", NotificationFeatures.Approvals));
        Assert.Equal("alice/_Settings/Notifications/odd-key-",
            NotificationFeaturePreferencePaths.PathFor("alice", "odd/key!"));
    }
}
