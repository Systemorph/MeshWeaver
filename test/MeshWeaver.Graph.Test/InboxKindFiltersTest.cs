using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Every inbox kind owns its OWN state filter: activities Open / Done, mail and chats Unread / Read. A read mail
/// is never "done", and the old global state choice must not hide it (owner, 2026-10-25).
/// </summary>
public class InboxKindFiltersTest
{
    private static InboxRow Row(string path, string category, string stateKind, string state = "", bool unread = false, params string[] tags) => new()
    {
        Path = path, Category = category, SourceId = category, StateKind = stateKind, State = state, Unread = unread,
        Tags = [.. tags], Stamp = "2026-10-21T06:22:13.0000000Z",
    };

    private static readonly InboxRow[] Rows =
    [
        Row("awaiting", "Approvals", InboxStateKind.Activity, InboxState.Awaiting),
        Row("decided", "Approvals", InboxStateKind.Activity, InboxState.Approved),
        Row("unread-mail", "Mail", InboxStateKind.Mail, InboxState.Unread, unread: true, "reply"),
        Row("read-mail", "Mail", InboxStateKind.Mail, string.Empty, unread: false, "reply", "act"),
        Row("read-chat", "Conversations", InboxStateKind.Chat),
        Row("unread-chat", "Conversations", InboxStateKind.Chat, InboxState.Unread, unread: true),
    ];

    private static ImmutableArray<InboxSection> Sections() =>
    [
        new(InboxBand.NeedsYou, "Approvals", 1, "q") { Category = "Approvals", DefaultKind = true, AlwaysOn = true },
        new(InboxBand.NeedsYou, "Outlook", 2, "q") { Category = "Mail" },
        new(InboxBand.NeedsYou, "Teams", 3, "q") { Category = "Conversations" },
    ];

    /// <summary>Every kind ticked, as a person who ticked them all would have.</summary>
    private static InboxPreferences AllKinds(InboxPreferences p)
        => p with { Kinds = p.Kinds.ToImmutableDictionary(k => k.Key, _ => true, StringComparer.Ordinal) };

    private static string[] Flat(ImmutableDictionary<string, bool> d) => [.. d.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")];

    private static string[] Paths(IEnumerable<InboxRow> rows) => [.. rows.Select(r => r.Path).Order(StringComparer.Ordinal)];

    [Fact]
    public void EachKind_DeclaresItsOwnVocabularyAndDefault()
    {
        InboxStateKind.Vocabulary(InboxStateKind.Activity).Should().Equal("open", "done");
        InboxStateKind.Vocabulary(InboxStateKind.Mail).Should().Equal("unread", "read");
        InboxStateKind.Vocabulary(InboxStateKind.Chat).Should().Equal("unread", "read");
        InboxStateKind.Vocabulary(InboxStateKind.Notification).Should().BeEmpty();
        Flat(InboxStateKind.Defaults(InboxStateKind.Activity)).Should().Equal("done=False", "open=True");
        InboxStateKind.Defaults(InboxStateKind.Mail).Values.Should().OnlyContain(on => on, "a read mail is listed by default");
        InboxStateKind.Resolve(null, "Mail").Should().Be(InboxStateKind.Mail);
        InboxStateKind.Resolve(null, "Conversations").Should().Be(InboxStateKind.Chat);
        InboxStateKind.Resolve(null, "Approvals").Should().Be(InboxStateKind.Activity);
        InboxStateKind.Resolve("chat", "Whatever").Should().Be(InboxStateKind.Chat, "a leg's own declaration wins");
        InboxStateKind.Resolve("nonsense", "Mail").Should().Be(InboxStateKind.Mail, "an unknown kind falls back to the category's, never to an empty filter");
        InboxRows.StateKindOf(Sections()[1]).Should().Be(InboxStateKind.Mail);
    }

    [Fact]
#pragma warning disable CS0618 // the retired forwarder
    public void TheRetiredStatesProperty_ForwardsToTheActivityFilter()
    {
        var filter = new InboxFilter { States = ImmutableDictionary<string, bool>.Empty.Add("open", true) };
        filter.ActivityStates.Should().ContainKey("open");
        filter.MailStates.Should().BeEmpty("it never touches mail");
    }
#pragma warning restore CS0618

    [Fact]
    public void TheDefaults_HoldOneFieldPerKind()
    {
        var defaults = InboxPreferencesNodeType.Defaults(Sections());
        defaults.ActivityStates["open"].Should().BeTrue();
        defaults.ActivityStates["done"].Should().BeFalse();
        Flat(defaults.MailStates).Should().Equal("read=True", "unread=True");
        Flat(defaults.ChatStates).Should().Equal("read=True", "unread=True");
        defaults.States.Should().BeEmpty("the global state filter is retired");
    }

    /// <summary>The read mail of the owner's report: under the default filters it is listed, together with the unread one and the open approval.</summary>
    [Fact]
    public void AReadMail_IsListedUnderTheDefaultFilters()
    {
        var filter = InboxLayoutArea.WithPreferences(new InboxFilter(), AllKinds(InboxPreferencesNodeType.Defaults(Sections())));
        Paths(InboxRows.Apply(Rows, filter)).Should().Equal("awaiting", "read-chat", "read-mail", "unread-chat", "unread-mail");
    }

    /// <summary>
    /// The owner's stored choice — the old global state filter open-only, mail ticked — maps to the activities filter
    /// ONLY: the read mail is listed, the decided approval is not.
    /// </summary>
    [Fact]
    public void TheOldGlobalStatesChoice_MapsToActivitiesOnly_AndNeverHidesMail()
    {
        var legacy = new InboxPreferences
        {
            Kinds = InboxPreferencesNodeType.Defaults(Sections()).Kinds.SetItem(InboxRows.SourceKey("Mail"), true),
            States = ImmutableDictionary<string, bool>.Empty.Add("open", true).Add("done", false),
        };
        var filter = InboxLayoutArea.WithPreferences(new InboxFilter(), legacy);
        filter.ActivityStates["open"].Should().BeTrue();
        filter.ActivityStates["done"].Should().BeFalse();
        filter.MailStates.Should().ContainKey("read").WhoseValue.Should().BeTrue();
        var listed = Paths(InboxRows.Apply(Rows, filter));
        listed.Should().Contain(["read-mail", "unread-mail", "awaiting"]).And.NotContain("decided");
        // And a person who had ticked Done keeps seeing decided approvals — the choice moved, it was not dropped.
        InboxLayoutArea.WithPreferences(new InboxFilter(), legacy with { States = legacy.States.SetItem("done", true) })
            .ActivityStates["done"].Should().BeTrue();
    }

    /// <summary>Done ticked before the migration was written must not drop the legacy "open": the edited key wins, the untouched legacy key stays.</summary>
    [Fact]
    public void AnEditBeforeTheMigration_KeepsTheLegacyOpen()
    {
        var edited = new InboxPreferences
        {
            States = ImmutableDictionary<string, bool>.Empty.Add("open", true).Add("done", false),
            ActivityStates = ImmutableDictionary<string, bool>.Empty.Add("done", true),
        };
        Flat(InboxPreferencesNodeType.Effective(edited).ActivityStates).Should().Equal("done=True", "open=True");
        Flat(InboxPreferencesNodeType.Completed(edited, InboxPreferencesNodeType.SeedForExisting(Sections()))!.ActivityStates).Should().Equal("done=True", "open=True");
    }

    /// <summary>A legacy map naming only Done meant "Done only": the migrated map says so, and stays the same on the next read.</summary>
    [Fact]
    public void ALegacyMapNamingOnlyDone_MigratesToDoneOnly_AndIsStable()
    {
        var legacy = new InboxPreferences { States = ImmutableDictionary<string, bool>.Empty.Add("done", true) };
        var once = InboxPreferencesNodeType.Effective(legacy);
        Flat(once.ActivityStates).Should().Equal("done=True", "open=False");
        once.States.Should().BeEmpty();
        Flat(InboxPreferencesNodeType.Effective(once).ActivityStates).Should().Equal("done=True", "open=False");
    }

    /// <summary>A mail or chat box ticked before the node was seeded leaves a partial map; the untouched key keeps its default.</summary>
    [Fact]
    public void APartialMailOrChatMap_KeepsTheDefaultOfTheUntouchedKey()
    {
        var edited = new InboxPreferences
        {
            MailStates = ImmutableDictionary<string, bool>.Empty.Add("unread", false),
            ChatStates = ImmutableDictionary<string, bool>.Empty.Add("read", false),
        };
        var effective = InboxPreferencesNodeType.Effective(edited);
        Flat(effective.MailStates).Should().Equal("read=True", "unread=False");
        Flat(effective.ChatStates).Should().Equal("read=False", "unread=True");
        InboxPreferencesNodeType.MissingStates(edited).Should().Equal("activity");
    }

    /// <summary>The stored node is migrated once: the legacy choice moves to the activities field, the other kinds get their defaults, and a second pass finds nothing to do.</summary>
    [Fact]
    public void TheStoredNode_IsMigratedOnce_AndConverges()
    {
        var expected = InboxPreferencesNodeType.SeedForExisting(Sections());
        var legacy = new InboxPreferences { States = ImmutableDictionary<string, bool>.Empty.Add("open", true).Add("done", false) };
        InboxPreferencesNodeType.MissingStates(legacy).Should().Equal("activity", "mail", "chat");
        var migrated = InboxPreferencesNodeType.Completed(legacy, expected);
        migrated.Should().NotBeNull();
        Flat(migrated!.ActivityStates).Should().Equal("done=False", "open=True");
        Flat(migrated.MailStates).Should().Equal("read=True", "unread=True");
        migrated.States.Should().BeEmpty();
        InboxPreferencesNodeType.Completed(migrated, expected).Should().BeNull("nothing is left to add: the seeding converges");
    }

    /// <summary>The filters are separate: ticking Done on activities, or unticking Read on mail, touches no other kind.</summary>
    [Fact]
    public void TheFilters_AreSeparate()
    {
        var all = AllKinds(InboxPreferencesNodeType.Defaults(Sections()));
        var doneToo = InboxLayoutArea.WithPreferences(new InboxFilter(), all with { ActivityStates = all.ActivityStates.SetItem("done", true) });
        Paths(InboxRows.Apply(Rows, doneToo)).Should().Contain("decided").And.Contain("read-mail");
        var unreadMailOnly = InboxLayoutArea.WithPreferences(new InboxFilter(), all with { MailStates = all.MailStates.SetItem("read", false) });
        var listed = Paths(InboxRows.Apply(Rows, unreadMailOnly));
        listed.Should().NotContain("read-mail").And.Contain("unread-mail", "unread is still ticked")
            .And.Contain("read-chat", "the chat filter is its own").And.Contain("awaiting");
    }

    /// <summary>The triage buckets (Reply / Act / …) of the mail stay available and narrow the list under the default filters.</summary>
    [Fact]
    public void TheTriageCategories_RenderUnderTheDefaultFilters()
    {
        var filter = InboxLayoutArea.WithPreferences(new InboxFilter(), AllKinds(InboxPreferencesNodeType.Defaults(Sections())));
        InboxRows.TagCounts(Rows).Select(t => t.Tag).Should().Contain(["reply", "act"]);
        var reply = InboxRows.Apply(Rows, filter with { TagKeys = [InboxRows.TagKey("reply")] });
        Paths(reply).Should().Equal("read-mail", "unread-mail");
        Paths(InboxRows.Apply(Rows, filter with { TagKeys = [InboxRows.TagKey("act")] })).Should().Equal("read-mail");
    }

    /// <summary>A read mail carries no state of its own — it is never badged "done".</summary>
    [Fact]
    public void AReadMail_HasNoDoneState()
    {
        var section = new InboxSection(InboxBand.NeedsYou, "Outlook", 2, "q") { Category = "Mail" };
        var row = InboxRows.Of(new MeshNode("m", "alice/_Mailbox/outlook") { Name = "m" }, section, null, null);
        row.StateKind.Should().Be(InboxStateKind.Mail);
        row.State.Should().BeEmpty();
        InboxRows.StateGroupOfRow(row).Should().Be("read");
        InboxRows.StateGroupOfRow(row with { Unread = true }).Should().Be("unread");
    }

    /// <summary>A state group is shown only for a kind that is connected AND enabled.</summary>
    [Fact]
    public void TheStateGroups_AreShownOnlyForEnabledKinds()
    {
        var sections = Sections();
        var none = ImmutableDictionary<string, bool>.Empty;
        InboxLayoutArea.StateStyles(sections, none).Values.Should().OnlyContain(style => style == string.Empty, "no kind ticked lists every kind");
        var onlyApprovals = none.Add(InboxRows.SourceKey("Approvals"), true).Add(InboxRows.SourceKey("Mail"), false);
        var styles = InboxLayoutArea.StateStyles(sections, onlyApprovals);
        styles[InboxStateKind.Activity].Should().BeEmpty();
        styles[InboxStateKind.Mail].Should().Be(InboxLayoutArea.HiddenStyle);
        styles[InboxStateKind.Chat].Should().Be(InboxLayoutArea.HiddenStyle);
    }
}
