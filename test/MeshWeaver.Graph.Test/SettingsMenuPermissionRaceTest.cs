using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The settings page must never render its menu through a STALE permission snapshot (#1962).
///
/// <para>The reported symptom was "User → Settings → the display-time-zone tab is absent" on
/// <c>memex.meshweaver.cloud</c> while the same build showed it locally, and the reporter's own
/// observation names the mechanism exactly: <i>"the Notifications entry beside it survives because
/// it requires <see cref="Permission.None"/>"</i>. A menu in which only the
/// <see cref="Permission.None"/> tabs survive is not a menu missing one tab — it is a menu rendered
/// for a viewer whose effective permissions read as <see cref="Permission.None"/>. And the gate
/// itself was measured to evaluate CORRECTLY on both installs, which is the puzzle: the gate is
/// right, and a render built from an earlier, lower value of it wins the race.</para>
///
/// <para>Both of the page's inputs are long-lived and enrich on their own schedule:
/// <c>PermissionEvaluator</c> emits a low seed (an empty assignment set folds to
/// <see cref="Permission.None"/>) before the synced-assignment answer lands, and a settings-tab
/// provider re-emits whenever its own live check settles (a global-admin probe, a GitHub call, a
/// cross-partition query). The old composition — <c>perms.SelectMany(p =&gt; items.Select(filter
/// through p))</c> — built one live provider chain PER permission value and unsubscribed none of
/// them, so the chain holding <see cref="Permission.None"/> re-rendered the whole menu the next
/// time any provider spoke.</para>
///
/// <para>🚨 The fix must not be "show it to everyone": <see cref="AReadOnlyViewer_NeverSeesAnUpdateGatedTab"/>
/// and <see cref="AReadOnlyViewer_NeverSeesAnUpdateGatedTab_HoweverTheStreamsInterleave"/> pin the
/// deny direction against exactly the interleavings the grant direction is fixed for.</para>
/// </summary>
public class SettingsMenuPermissionRaceTest
{
    private const string PreferencesTabId = "Preferences";
    private const string NotificationsTabId = "Notifications";
    private const string MetadataTabId = "Metadata";

    /// <summary>A stand-in for the real menu: one tab per permission class the page carries.</summary>
    private static IReadOnlyList<SettingsMenuItemDefinition> AllTabs() =>
    [
        Tab(MetadataTabId, Permission.Read, 0),
        // The display-time-zone tab — UserNodeType.AddUserPreferencesSettingsTab.
        Tab(PreferencesTabId, Permission.Update, 50),
        // The Permission.None entry beside it; the survivor that named the bug.
        Tab(NotificationsTabId, Permission.None, 60),
    ];

    private static SettingsMenuItemDefinition Tab(string id, Permission required, int order)
        => new(id, id, (_, stack, _) => stack, RequiredPermission: required, Order: order);

    private static IReadOnlyList<string> Ids(IReadOnlyList<SettingsMenuItemDefinition> items)
        => items.Select(i => i.Id).ToList();

    // ---------------------------------------------------------------------------------------
    // The pure gate — both directions.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The owner of a user node holds Update there (<c>UserScopeGrantHandler</c> writes the
    /// self-Admin assignment), so the display-time-zone tab is theirs.
    /// </summary>
    [Fact]
    public void AnOwnerWithUpdate_SeesTheDisplayTimeZoneTab()
        => Ids(SettingsMenuItemsExtensions.FilterByPermission(
                AllTabs(), Permission.Read | Permission.Update))
            .Should().Contain(PreferencesTabId);

    /// <summary>
    /// 🚨 The deny direction. A viewer who may read the page but not write it must NOT be handed
    /// the editor for someone else's time zone and language — so the fix can never be "stop
    /// filtering".
    /// </summary>
    [Fact]
    public void AReadOnlyViewer_NeverSeesAnUpdateGatedTab()
    {
        var visible = Ids(SettingsMenuItemsExtensions.FilterByPermission(AllTabs(), Permission.Read));
        visible.Should().Contain(MetadataTabId, "Read is what the Metadata tab asks for");
        visible.Should().NotContain(PreferencesTabId,
            "the display-time-zone tab edits the node and requires Update");
    }

    /// <summary>
    /// The fingerprint from the report, pinned: with <see cref="Permission.None"/> the ONLY
    /// survivors are the <see cref="Permission.None"/> tabs. Anyone who sees that shape on a live
    /// portal is looking at a viewer whose permissions read as None, not at a missing tab.
    /// </summary>
    [Fact]
    public void WithNoPermissions_OnlyThePermissionNoneTabsSurvive()
        => Ids(SettingsMenuItemsExtensions.FilterByPermission(AllTabs(), Permission.None))
            .Should().Equal(NotificationsTabId);

    /// <summary>
    /// The tab really is Update-gated, so "make it appear" can never be quietly implemented by
    /// lowering its requirement.
    /// </summary>
    [Fact]
    public void TheShippedPreferencesTab_IsGatedOnUpdate()
    {
        var tab = AllTabs().Single(t => t.Id == PreferencesTabId);
        tab.RequiredPermission.Should().Be(Permission.Update,
            "UserNodeType.AddUserPreferencesSettingsTab declares Permission.Update — this test is "
            + "about WHICH render wins, never about relaxing the gate");
    }

    // ---------------------------------------------------------------------------------------
    // The ordering seam — the defect itself.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 🚨 THE REGRESSION. Permissions enrich from a low seed to the real answer; THEN a provider
    /// re-emits (its live check settled). The menu must still be the one the real permissions
    /// produce — not the seed's.
    ///
    /// <para>Against the pre-fix composition the last emission is the stale chain's, so the
    /// display-time-zone tab is gone and only the <see cref="Permission.None"/> entry is left:
    /// the reported symptom, reproduced deterministically.</para>
    /// </summary>
    [Fact]
    public void AProviderReEmission_DoesNotRevertToAStalePermissionSnapshot()
    {
        var node = new BehaviorSubject<MeshNode>(new MeshNode("alice") { NodeType = "User" });
        var perms = new BehaviorSubject<Permission>(Permission.None);
        var items = new BehaviorSubject<IReadOnlyList<SettingsMenuItemDefinition>>(AllTabs());

        var seen = new List<(Permission Perms, IReadOnlyList<string> Ids)>();
        using var _ = SettingsLayoutArea
            .MenuRenderInputs(node, perms, items)
            .Subscribe(t => seen.Add((t.Perms, Ids(t.Items))));

        // The synced assignment query answers: the owner really does hold Update.
        perms.OnNext(Permission.Read | Permission.Update);
        var afterEnrichment = seen.Count - 1;
        seen[^1].Ids.Should().Contain(PreferencesTabId, "the enriched permissions must reach the menu");

        // A provider's live check settles and it re-emits the same tab set. Nothing about the
        // viewer changed — so NO emission from here on may be built from the old value.
        items.OnNext(AllTabs());

        // 🚨 Asserting only the LAST emission would prove nothing: with one chain per permission
        // value BOTH chains emit, and which lands last is exactly the race. The invariant is that
        // a stale render is never produced AT ALL.
        var later = seen.Skip(afterEnrichment).ToList();
        later.Should().OnlyContain(e => e.Perms == (Permission.Read | Permission.Update),
            "once the permissions have enriched, every subsequent render must be folded with the "
            + "LATEST value; a render carrying the earlier Permission.None is a stale chain that "
            + "was never unsubscribed (#1962)");
        later.Should().OnlyContain(e => e.Ids.Contains(PreferencesTabId),
            "a menu that drops the Update-gated display-time-zone tab while the viewer holds "
            + "Update is the reported symptom — and it leaves only the Permission.None entries, "
            + "which is exactly how the cloud portal was described");
    }

    /// <summary>
    /// The same interleaving in the deny direction: a viewer whose permissions enrich from None to
    /// Read only must not gain the Update-gated tab from any ordering of the inputs.
    /// </summary>
    [Fact]
    public void AReadOnlyViewer_NeverSeesAnUpdateGatedTab_HoweverTheStreamsInterleave()
    {
        var node = new BehaviorSubject<MeshNode>(new MeshNode("alice") { NodeType = "User" });
        var perms = new BehaviorSubject<Permission>(Permission.None);
        var items = new BehaviorSubject<IReadOnlyList<SettingsMenuItemDefinition>>(AllTabs());

        var seen = new List<IReadOnlyList<string>>();
        using var _ = SettingsLayoutArea
            .MenuRenderInputs(node, perms, items)
            .Subscribe(t => seen.Add(Ids(t.Items)));

        perms.OnNext(Permission.Read);
        items.OnNext(AllTabs());
        node.OnNext(new MeshNode("alice") { NodeType = "User", Name = "Alice" });
        perms.OnNext(Permission.Read);

        seen.Should().OnlyContain(ids => !ids.Contains(PreferencesTabId),
            "no interleaving may hand an Update-gated tab to a viewer who only holds Read");
    }

    /// <summary>
    /// <c>CanEdit</c> (the read-only banner above the page) rides the same fold, so it can never
    /// disagree with the menu it is rendered beside.
    /// </summary>
    [Fact]
    public void CanEdit_TracksTheLatestPermissions()
    {
        var node = new BehaviorSubject<MeshNode>(new MeshNode("alice") { NodeType = "User" });
        var perms = new BehaviorSubject<Permission>(Permission.None);
        var items = new BehaviorSubject<IReadOnlyList<SettingsMenuItemDefinition>>(AllTabs());

        var seen = new List<bool>();
        using var _ = SettingsLayoutArea
            .MenuRenderInputs(node, perms, items)
            .Subscribe(t => seen.Add(t.CanEdit));

        seen[^1].Should().BeFalse();
        perms.OnNext(Permission.Read | Permission.Update);
        var afterEnrichment = seen.Count - 1;
        items.OnNext(AllTabs());

        seen.Skip(afterEnrichment).Should().OnlyContain(canEdit => canEdit,
            "a provider re-emission must not revoke the viewer's edit rights — the read-only "
            + "banner rides the same fold as the menu, so it can never disagree with it");
    }

    /// <summary>
    /// The provider chain is subscribed ONCE for the life of the area, not once per permission
    /// value. The old shape re-subscribed every provider on each permission emission — so a busy
    /// page accumulated a full provider fan-out (global-admin probes, GitHub calls,
    /// cross-partition queries) per enrichment, and every one of them stayed live to re-render the
    /// menu through the snapshot it was born with.
    /// </summary>
    [Fact]
    public void TheProviderChain_IsSubscribedOncePerArea_NotOncePerPermissionValue()
    {
        var subscriptions = 0;
        var node = new BehaviorSubject<MeshNode>(new MeshNode("alice") { NodeType = "User" });
        var perms = new BehaviorSubject<Permission>(Permission.None);
        var items = Observable.Defer(() =>
        {
            subscriptions++;
            return Observable.Return(AllTabs());
        });

        using var _ = SettingsLayoutArea
            .MenuRenderInputs(node, perms, items)
            .Subscribe(_ => { });

        perms.OnNext(Permission.Read);
        perms.OnNext(Permission.Read | Permission.Update);

        subscriptions.Should().Be(1,
            "the settings tabs are ONE live stream folded with the permissions, not a new provider "
            + "fan-out per permission value");
    }
}
