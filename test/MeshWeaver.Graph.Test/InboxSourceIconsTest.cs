using System.Collections.Immutable;
using MeshWeaver.Mesh;
using System.Xml.Linq;
using MeshWeaver.Domain;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox's first column shows the MEDIUM an item came through, and the toolbar offers an act
/// only where there is something to act on.
/// </summary>
public class InboxSourceIconsTest
{
    /// <summary>Every source id a built-in or channel leg registers, today.</summary>
    private static readonly string[] Sources =
        ["Approvals", "Notifications", "Activities", "Mail", "Chat", "Conversations", "Outlook", "Gmail", "iMessage", "WhatsApp", "Teams"];

    /// <summary>Every known source has its own, renderable symbol — none is a bare emoji any more.</summary>
    [Fact]
    public void EveryKnownSource_HasItsOwnRenderableSymbol()
    {
        InboxSourceIcons.KnownSources.Should().Contain(Sources, "the symbol is keyed by the leg's source id");
        foreach (var source in Sources)
        {
            var icon = InboxSourceIcons.Resolve(source, "✅");
            icon.Should().NotBe("✅", $"{source}: the table wins over a leg's emoji");
            Icon.Parse(icon)!.Provider.Should().BeOneOf([Icon.FluentProvider, Icon.InlineSvgProvider], $"{source} is a Fluent symbol or an inline mark");
        }
        Sources.Select(s => InboxSourceIcons.Resolve(s, null)).Should().OnlyHaveUniqueItems("one source, one look");
    }

    /// <summary>The outside channels the inbox legs declare (Outlook, Gmail, iMessage, WhatsApp, Teams).</summary>
    private static readonly string[] OutsideChannels = ["Outlook", "Gmail", "iMessage", "WhatsApp", "Teams"];

    /// <summary>The inline marks are well-formed, self-contained SVG documents with a 24x24 viewBox.</summary>
    [Fact]
    public void TheInlineMarks_AreWellFormedSelfContainedSvg_WithA24ViewBox()
    {
        foreach (var source in OutsideChannels)
            AssertSafeMark(source, InboxSourceIcons.Resolve(source, null));
    }

    /// <summary>The checks every mark must pass — also used to prove they fail on a broken entry.</summary>
    internal static void AssertSafeMark(string source, string svg)
    {
        var root = XDocument.Parse(svg).Root!;
        root.Name.LocalName.Should().Be("svg", source);
        root.Attribute("viewBox")?.Value.Should().Be("0 0 24 24", $"{source}: sized to a 24x24 viewBox");
        var all = root.DescendantsAndSelf().ToArray();
        all.Select(e => e.Name.LocalName).Should().NotContain(["script", "image", "foreignObject", "iframe"], $"{source}: self-contained, nothing executable or raster");
        foreach (var href in all.SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName == "href"))
            href.Value.Should().StartWith("#", $"{source}: no external reference");
        all.SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty($"{source}: no event handlers");
        svg.ToLowerInvariant().Should().NotContain("javascript:", $"{source}");
        svg.ToLowerInvariant().Should().NotContain("url(http", $"{source}: no remote paint server");
    }

    /// <summary>Revert-check: the safety checks really reject a broken entry.</summary>
    [Fact]
    public void ABrokenMark_FailsTheChecks()
    {
        var good = InboxSourceIcons.Resolve("Gmail", null);
        var broken = new[]
        {
            good.Replace("viewBox=\"0 0 24 24\"", "viewBox=\"0 0 32 32\""),
            good.Replace("</svg>", "<script>alert(1)</script></svg>"),
            good.Replace("</svg>", "<image href=\"https://example.com/x.png\"/></svg>"),
            good.Replace("<path ", "<path onclick=\"x()\" "),
            good[..^6],
        };
        foreach (var svg in broken)
            Assert.ThrowsAny<Exception>(() => AssertSafeMark("Gmail", svg));
    }

    /// <summary>The table covers every outside-channel source id the legs declare.</summary>
    [Fact]
    public void EveryOutsideChannelLeg_IsCovered()
    {
        var table = InboxSourceIcons.KnownSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        table.Should().Contain(OutsideChannels);
        foreach (var source in OutsideChannels)
            Icon.Parse(InboxSourceIcons.Resolve(source, null))!.Provider.Should().Be(Icon.InlineSvgProvider, $"{source} shows its real logo");
    }

    /// <summary>Lookup ignores case; an unknown source keeps its leg's symbol, and with none gets the fallback.</summary>
    [Fact]
    public void AnUnknownSource_KeepsItsDeclaredSymbol_ElseTheFallback()
    {
        InboxSourceIcons.Resolve("WHATSAPP", null).Should().Be(InboxSourceIcons.Resolve("WhatsApp", null));
        InboxSourceIcons.Resolve("Telegram", "📣").Should().Be("📣");
        InboxSourceIcons.Resolve("Telegram", null).Should().Be(InboxSourceIcons.Fallback);
        InboxSourceIcons.Resolve("Telegram", "  ").Should().Be(InboxSourceIcons.Fallback);
        InboxSourceIcons.Resolve(null, null).Should().Be(InboxSourceIcons.Fallback);
    }

    /// <summary>Only a text glyph may be spliced into a label.</summary>
    [Fact]
    public void OnlyAGlyph_IsUsableAsATextPrefix()
    {
        InboxSourceIcons.Glyph("📣").Should().Be("📣");
        InboxSourceIcons.Glyph(InboxSourceIcons.Resolve("Teams", null)).Should().BeEmpty();
        InboxSourceIcons.Glyph("Mail").Should().BeEmpty();
        InboxSourceIcons.Glyph(null).Should().BeEmpty();
    }

    /// <summary>The page resolves a leg's symbol through the table.</summary>
    [Fact]
    public void ASection_CarriesTheResolvedSymbol()
    {
        var leg = new MeshWeaver.Mesh.InboxQueryLeg
        {
            Source = "WhatsApp", SourceIcon = "🟢",
            Query = $"namespace:{InboxQueries.ViewerToken}/_Thread {InboxQueries.RowProjection}",
        };
        InboxLayoutArea.Sections([leg], "alice").Should().ContainSingle()
            .Which.SourceIcon.Should().Be(InboxSourceIcons.Resolve("WhatsApp", null));
    }

    private const int NeedsYou = 0;

    private static InboxRow Row(string source, string name, int rank = NeedsYou)
        => new() { Path = "alice/" + name, Title = name, SourceId = source, Rank = rank };

    /// <summary>Approve / Reject show for approvals the viewer decides — never for a mailbox.</summary>
    [Fact]
    public void ApproveAndReject_ShowOnlyWhereThereIsAnApprovalToDecide()
    {
        var approval = Row(InboxQueries.ApprovalsSource, "a1");
        var mail = Row("Outlook", "m1");
        var decided = Row(InboxQueries.ApprovalsSource, "a2", rank: 2);

        InboxLayoutArea.ToolbarOf([approval, mail], [], "alice", NeedsYou).Decide.Should().BeTrue("an approval is listed");
        InboxLayoutArea.ToolbarOf([mail, Row("Gmail", "m2")], [], "alice", NeedsYou).Decide.Should().BeFalse("a mail-only view");
        InboxLayoutArea.ToolbarOf([], [], "alice", NeedsYou).Decide.Should().BeFalse("an empty list");
        InboxLayoutArea.ToolbarOf([mail], [], "alice", null).Decide.Should().BeFalse("a viewer who decides no approvals has no such band");
        InboxLayoutArea.ToolbarOf([approval], [], "alice", null).Decide.Should().BeFalse("no obligations band, nothing to decide");
        InboxLayoutArea.ToolbarOf([decided, mail], [], "alice", NeedsYou).Decide.Should().BeFalse("a decided approval in the ledger is not a request");
    }

    /// <summary>With rows ticked, only those count.</summary>
    [Fact]
    public void ASelection_NarrowsTheToolbarToTheTickedRows()
    {
        var approval = Row(InboxQueries.ApprovalsSource, "a1");
        var mail = Row("Outlook", "m1");
        InboxLayoutArea.ToolbarOf([approval, mail], [mail.Path], "alice", NeedsYou).Decide.Should().BeFalse("only the mail is ticked");
        InboxLayoutArea.ToolbarOf([approval, mail], [approval.Path], "alice", NeedsYou).Decide.Should().BeTrue("the approval is ticked");
        InboxLayoutArea.ToolbarOf([approval, mail], ["alice/gone"], "alice", NeedsYou).Decide.Should().BeFalse("a ticked key that names no row counts for nothing");
        InboxLayoutArea.ToolbarOf([approval with { Path = "bob/a1" }], ["bob/a1"], "alice", NeedsYou).Decide.Should().BeFalse("outside the owner's partition");
    }
}

/// <summary>The lifecycle state each kind of item shows, and the multi-select kind / tag filters.</summary>
public class InboxStateAndFilterTest
{
    /// <summary>Approvals wait, then are approved or rejected; only done states carry a check.</summary>
    [Fact]
    public void AnApproval_IsAwaiting_UntilItIsDecided()
    {
        InboxState.Of("Approvals", InboxBand.NeedsYou, false, false, null).Should().Be(InboxState.Awaiting);
        InboxState.Of("Approvals", InboxBand.Running, true, false, null).Should().Be(InboxState.Awaiting, "asked by me, still pending");
        InboxState.Of("Approvals", InboxBand.Recent, false, false, "Approved").Should().Be(InboxState.Approved);
        InboxState.Of("Approvals", InboxBand.Recent, false, false, "Rejected").Should().Be(InboxState.Rejected);
        InboxState.Of("Approvals", InboxBand.Recent, false, false, "Expired").Should().Be(InboxState.Expired);
        InboxState.Of("Approvals", InboxBand.Recent, false, false, null).Should().BeEmpty("nothing says what became of it");
        InboxState.Mark(InboxState.Awaiting).Should().BeEmpty("a waiting request must not look finished");
        InboxState.Mark(InboxState.Approved).Should().Be("✓ ");
        InboxState.Mark(InboxState.Done).Should().Be("✓ ");
        InboxState.Mark(InboxState.Failed).Should().Be("✕ ");
        InboxState.All.Where(s => InboxState.Mark(s).Contains('✓')).Should().Equal(InboxState.Approved, InboxState.Done);
    }

    /// <summary>Activities show their real state.</summary>
    [Fact]
    public void AnActivity_ShowsItsRealState()
    {
        InboxState.Of("Activities", InboxBand.Running, false, false, null).Should().Be(InboxState.Running);
        InboxState.Of("Activities", InboxBand.NeedsYou, false, false, null).Should().Be(InboxState.Failed);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Succeeded").Should().Be(InboxState.Done);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Warning").Should().Be(InboxState.Done);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Cancelled").Should().Be(InboxState.Cancelled);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Requested").Should().Be(InboxState.Requested);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Failed").Should().Be(InboxState.Failed);
    }

    /// <summary>Mail and notifications show Unread, or nothing.</summary>
    [Fact]
    public void MailAndNotifications_AreUnreadOrNothing()
    {
        InboxState.Of("Outlook", InboxBand.NeedsYou, false, true, null).Should().Be(InboxState.Unread);
        InboxState.Of("Outlook", InboxBand.NeedsYou, false, false, null).Should().BeEmpty();
        InboxState.Of("Notifications", InboxBand.NeedsYou, false, false, "Sent").Should().BeEmpty("an unknown status is not part of the vocabulary");
        InboxState.All.Select(InboxState.Color).Should().OnlyContain(c => c.Length > 0, "every state has its own colour");
        InboxState.Color(InboxState.Awaiting).Should().NotBe(InboxState.Color(InboxState.Approved));
    }

    private static InboxRow Row(string name, string category, params string[] tags)
        => new() { Path = "alice/" + name, Title = name, SourceId = category, Category = category, Stamp = name, Tags = [.. tags] };

    /// <summary>Ticking several kinds lists their union; ticking none lists all.</summary>
    [Fact]
    public void SeveralKinds_ListTheirUnion_AndNoneListsAll()
    {
        InboxRow[] rows = [Row("a", "Approvals"), Row("b", "Notifications"), Row("c", "Conversations")];
        InboxFilter Ticked(params string[] kinds) => new()
        {
            Kinds = kinds.ToImmutableDictionary(InboxRows.SourceKey, _ => true),
        };
        InboxRows.Apply(rows, Ticked("Approvals", "Conversations")).Select(r => r.Title).OrderBy(t => t).Should().Equal("a", "c");
        InboxRows.Apply(rows, Ticked("Notifications")).Select(r => r.Title).Should().Equal("b");
        InboxRows.Apply(rows, new InboxFilter()).Should().HaveCount(3);
        InboxRows.Apply(rows, new InboxFilter { Kinds = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Approvals"), false) })
            .Should().HaveCount(3, "an unticked box narrows nothing");
        InboxRows.Fingerprint(Ticked("Approvals")).Should().NotBe(InboxRows.Fingerprint(new InboxFilter()));
    }

    /// <summary>Ticked tags list the union; none ticked lists all.</summary>
    [Fact]
    public void TickedTags_ListTheirUnion_AndNoneListsAll()
    {
        InboxRow[] rows = [Row("a", "Mail", "reply"), Row("b", "Mail", "act"), Row("c", "Mail", "promo"), Row("d", "Mail")];
        InboxRows.Apply(rows, new InboxFilter { TagKeys = [InboxRows.TagKey("reply"), InboxRows.TagKey("act")] })
            .Select(r => r.Title).OrderBy(t => t).Should().Equal("a", "b");
        InboxRows.Apply(rows, new InboxFilter()).Should().HaveCount(4, "no tag ticked: all tags");
        InboxRows.Fingerprint(new InboxFilter { TagKeys = [InboxRows.TagKey("reply")] }).Should().NotBe(InboxRows.Fingerprint(new InboxFilter()));
    }

    /// <summary>The tag checkboxes keep a ticked box ticked and put Important first.</summary>
    [Fact]
    public void TheTagCheckboxes_KeepWhatWasTicked()
    {
        var counts = new[] { (InboxRows.ImportantKey, string.Empty, 2), (InboxRows.TagKey("reply"), "reply", 3) };
        var items = InboxLayoutArea.TagItems(counts, "Important", [InboxRows.TagKey("reply")]);
        items.Select(i => (i.Key, i.Checked)).Should().Equal((InboxRows.ImportantKey, false), (InboxRows.TagKey("reply"), true));
        items[0].Label.Should().Contain("Important").And.Contain("2");
        items[1].Label.Should().Be("#reply  3");
    }

    private static InboxRow ActivityRow(object? content)
    {
        var node = new MeshNode("run1", "alice/_Activity") { Name = "Import", NodeType = "Activity" };
        if (content is not null)
            node = node with { Content = System.Text.Json.JsonSerializer.SerializeToElement(content) };
        return InboxRows.Of(node, new InboxSection(InboxBand.Running, "Activities", 10, "q"), null, null);
    }

    /// <summary>A running activity shows its step out of its sub-activities and its latest log line.</summary>
    [Fact]
    public void ARunningActivity_ShowsItsStepAndLatestLogLine()
    {
        var row = ActivityRow(new
        {
            subActivities = new object[]
            {
                new { status = "Succeeded" }, new { status = "Warning" }, new { status = 1 }, new { status = "Running" }, new { status = "Running" },
            },
            messages = new object[] { new { message = "first" }, new { message = "Building the model\nsecond line" } },
        });
        row.State.Should().Be(InboxState.Running);
        row.StepNumber.Should().Be("4/5", "three finished sub-activities (by name and by number), the fourth is running");
        row.StepMessage.Should().Be("Building the model", "the first line of the latest message");
        var texts = new[] { InboxState.Running }.ToImmutableDictionary(s => s, _ => "Running");
        InboxLayoutArea.WithStateText(row, texts, "step {0}").StateText.Should().Be("Running · step 4/5: Building the model");
    }

    /// <summary>Each part of the step is optional; without either the badge is the plain state.</summary>
    [Fact]
    public void AnActivity_WithoutAStep_ShowsThePlainState()
    {
        var texts = new[] { InboxState.Running }.ToImmutableDictionary(s => s, _ => "Running");
        InboxLayoutArea.WithStateText(ActivityRow(new { status = "Running" }), texts, "step {0}").StateText.Should().Be("Running");
        InboxLayoutArea.WithStateText(ActivityRow(null), texts, "step {0}").StateText.Should().Be("Running", "a leg that dropped content");
        InboxLayoutArea.WithStateText(ActivityRow(new { messages = new object[] { new { message = "Working" } } }), texts, "step {0}")
            .StateText.Should().Be("Running · Working");
        InboxLayoutArea.WithStateText(ActivityRow(new { subActivities = new object[] { new { status = "Running" } } }), texts, "step {0}")
            .StateText.Should().Be("Running · step 1/1");
        ActivityRow(new { messages = new object[] { new { message = new string('x', 200) } } }).StepMessage
            .Should().HaveLength(InboxRows.StepMessageLimit).And.EndWith("…");
    }

    /// <summary>Children may overlap and finish out of order: the step is the first unfinished one.</summary>
    [Fact]
    public void TheStep_IsTheFirstUnfinishedSubActivity_WhenTheyFinishOutOfOrder()
    {
        ActivityRow(new { subActivities = new object[] { new { status = "Running" }, new { status = "Succeeded" }, new { status = "Running" } } })
            .StepNumber.Should().Be("1/3");
        ActivityRow(new { subActivities = new object[] { new { status = "Succeeded" }, new { status = "Running" }, new { status = "Succeeded" } } })
            .StepNumber.Should().Be("2/3");
        ActivityRow(new { subActivities = new object[] { new { status = "Succeeded" }, new { status = "Failed" } } })
            .StepNumber.Should().Be("2/2", "never past the last");
    }

    /// <summary>A localizable log entry is read in the viewer's locale; the English text is the fallback.</summary>
    [Fact]
    public void TheStepMessage_IsLocalizedForTheViewer()
    {
        var node = new MeshNode("run3", "alice/_Activity") { NodeType = "Activity", Content = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            messages = new object[] { new { message = "Running", logLevel = "Information", messageKey = "inbox.state.running" } },
        }) };
        var section = new InboxSection(InboxBand.Running, "Activities", 10, "q");
        InboxRows.Of(node, section, null, null, null, 0, null, null, null, "de").StepMessage.Should().Be("Läuft");
        InboxRows.Of(node, section, null, null, null, 0, null, null, null, null).StepMessage.Should().Be("Running", "no locale: the English fallback");
    }

    /// <summary>Only a running activity carries a step; a failed one and a mail do not.</summary>
    [Fact]
    public void OnlyARunningActivity_CarriesAStep()
    {
        var content = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "Failed", messages = new object[] { new { message = "boom" } } });
        var node = new MeshNode("run2", "alice/_Activity") { NodeType = "Activity", Content = content };
        var failed = InboxRows.Of(node, new InboxSection(InboxBand.NeedsYou, "Activities", 50, "q"), null, null);
        failed.State.Should().Be(InboxState.Failed);
        failed.StepMessage.Should().BeEmpty();
        InboxRows.Of(node, new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q"), null, null).StepMessage.Should().BeEmpty();
    }

    /// <summary>The running-activities leg keeps content, so the step can be read.</summary>
    [Fact]
    public void TheRunningActivitiesLeg_KeepsContentInItsProjection()
    {
        var leg = InboxQueries.BuiltIn.Single(l => l.Source == "Activities" && l.Band == InboxBand.Running);
        leg.TimeField.Should().Be("start");
        InboxQueries.Bounded(leg.Query, leg.TimeField).Should().Contain("content");
        leg.Query.Should().Contain("sort:content.start-desc", "the bound is chosen by the stamp the rows are shown by");
    }

    private static InboxRow StateRow(string name, string state)
        => new() { Path = "alice/" + name, Title = name, SourceId = "Mail", Category = "Mail", Stamp = name, State = state };

    private static readonly InboxRow[] StateRows =
    [
        StateRow("awaiting", InboxState.Awaiting), StateRow("requested", InboxState.Requested), StateRow("running", InboxState.Running),
        StateRow("unread", InboxState.Unread), StateRow("approved", InboxState.Approved), StateRow("rejected", InboxState.Rejected),
        StateRow("expired", InboxState.Expired), StateRow("done", InboxState.Done), StateRow("failed", InboxState.Failed),
        StateRow("cancelled", InboxState.Cancelled), StateRow("stateless", string.Empty),
    ];

    private static InboxFilter Groups(params string[] groups)
        => new() { States = groups.ToImmutableDictionary(g => g, _ => true) };

    /// <summary>Open lists what still waits; Done what is over; both everything; none everything.</summary>
    [Fact]
    public void TheStateFilter_OpenDoneBothAndNone()
    {
        string[] open = ["awaiting", "requested", "running", "unread"];
        string[] done = ["approved", "rejected", "expired", "done", "failed", "cancelled", "stateless"];
        InboxRows.Apply(StateRows, Groups(InboxRows.OpenKey)).Select(r => r.Title).Order().Should().Equal(open.Order());
        InboxRows.Apply(StateRows, Groups(InboxRows.DoneKey)).Select(r => r.Title).Order().Should().Equal(done.Order());
        InboxRows.Apply(StateRows, Groups(InboxRows.OpenKey, InboxRows.DoneKey)).Should().HaveCount(StateRows.Length);
        InboxRows.Apply(StateRows, new InboxFilter()).Should().HaveCount(StateRows.Length, "none ticked: all");
        InboxRows.Apply(StateRows, new InboxFilter { States = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.OpenKey, false) })
            .Should().HaveCount(StateRows.Length, "an unticked box narrows nothing");
        InboxRows.Apply(StateRows, Groups(InboxRows.OpenKey) with { Category = "Mail" }).Should().HaveCount(open.Length, "it narrows a picked category too");
        InboxRows.Fingerprint(Groups(InboxRows.OpenKey)).Should().NotBe(InboxRows.Fingerprint(new InboxFilter()))
            .And.NotBe(InboxRows.Fingerprint(Groups(InboxRows.DoneKey)));
        InboxState.All.Should().OnlyContain(s => open.Contains(s) ? InboxRows.StateGroupOf(s) == InboxRows.OpenKey : InboxRows.StateGroupOf(s) == InboxRows.DoneKey);
    }
}
