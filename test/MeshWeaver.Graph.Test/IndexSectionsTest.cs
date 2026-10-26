using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The default index groups a level that mixes kinds of node into sections
/// (<see cref="IndexSections"/>) — asked for on a CRM client space (2026-10-26), whose root listed a
/// deal, mails, interactions, contacts and documents in one flat list: "add titles such as Mails to
/// give a better overview of the material". A deal's own level gets its sections the same way.
/// </summary>
public class IndexSectionsTest
{
    private const string Client = "Globex";

    private static MeshNode Node(string path, string nodeType, int? order = null)
    {
        var i = path.LastIndexOf('/');
        return new MeshNode(path[(i + 1)..], i < 0 ? "" : path[..i])
        { Name = path[(i + 1)..], NodeType = nodeType, Order = order };
    }

    /// <summary>A client space: a deal with its own mail and requirements, then contacts and a document.</summary>
    private static IReadOnlyCollection<MeshNode> ClientSpace() =>
    [
        Node(Client, "Crm/Counterparty"),
        Node(Client + "/Deal", "Crm/Opportunity", 1),
        Node(Client + "/Deal/ReplyMail", "Essentials/Email", 1),
        Node(Client + "/Deal/Requirements", "Markdown", 2),
        Node(Client + "/Deal/Response", "Markdown", 3),
        Node(Client + "/JaneRoe", "Crm/Contact", 2),
        Node(Client + "/MaxMuster", "Crm/Contact", 3),
        Node(Client + "/Notes", "Markdown", 4),
    ];

    [Fact]
    public void AMixedLevelReadsAsOneSectionPerKind_InTheLevelsOrder()
    {
        var nav = DefaultNodeNavigation.Build(Client, ClientSpace(), Client + "/JaneRoe")!;

        nav.Entries.Should().OnlyContain(e => e.IsSection, "the client's root mixes deals, contacts and documents");
        nav.Entries.Select(e => e.Label).Should().Equal(["Opportunities", "Contacts", "Documents"],
            "a section takes its first member's place in the level's order");
        nav.Entries[1].Children.Select(c => c.Path).Should().Equal(Client + "/JaneRoe", Client + "/MaxMuster");
        nav.Entries[1].Children[0].IsCurrent.Should().BeTrue("the page being read is still marked inside its section");
    }

    [Fact]
    public void ADealsOwnLevelHasItsOwnSections()
    {
        var nav = DefaultNodeNavigation.Build(Client, ClientSpace(), Client + "/Deal/ReplyMail")!;

        var deal = nav.Entries[0].Children.Single();
        deal.Path.Should().Be(Client + "/Deal");
        deal.Children.Select(e => e.Label).Should().Equal(["Emails", "Documents"]);
        deal.Children.Should().OnlyContain(e => e.IsSection);
        deal.Children[1].Children.Select(c => c.Path).Should().Equal(Client + "/Deal/Requirements", Client + "/Deal/Response");
    }

    [Fact]
    public void ALevelOfOneKindStaysFlat()
    {
        var docs = new[]
        {
            Node("Doc", "Markdown"), Node("Doc/A", "Markdown", 1), Node("Doc/B", "Markdown", 2),
        };

        var nav = DefaultNodeNavigation.Build("Doc", docs, "Doc/A")!;

        nav.Entries.Select(e => e.Path).Should().Equal("Doc/A", "Doc/B");
        nav.Entries.Should().OnlyContain(e => !e.IsSection, "a docs tree gains no heading");
    }

    [Fact]
    public void ASectionIsAnOpenGroupWithNoLinkOfItsOwn()
    {
        var nav = DefaultNodeNavigation.Build(Client, ClientSpace(), Client + "/JaneRoe")!;

        var rail = SuppliedNavigationRail.Plan(nav, Client + "/JaneRoe");

        var contacts = rail.Items[1].Should().BeOfType<SuppliedNavigationRail.RailGroup>().Subject;
        contacts.Label.Should().Be("Contacts");
        contacts.Expanded.Should().BeTrue("a section organises the level, it does not hide it");
        contacts.Items.OfType<SuppliedNavigationRail.RailLink>().Select(l => l.Path)
            .Should().Equal([Client + "/JaneRoe", Client + "/MaxMuster"],
                "a section holds only its members — no link of its own, there is no page behind it");
        contacts.Items.Should().HaveCount(2);
    }

    [Fact]
    public void AnOpenedFolderIsSectionedLikeALoadedLevel()
    {
        var entries = DefaultNodeNavigation.FolderEntries(Client + "/Deal",
            [.. ClientSpace().Where(n => n.Namespace == Client + "/Deal")], Client + "/Other",
            ImmutableHashSet<string>.Empty, "Show all");

        entries.Select(e => e.Label).Should().Equal(["Emails", "Documents"]);
    }

    [Theory]
    [InlineData("Crm/Opportunity", "Opportunities")]
    [InlineData("Crm/Contact", "Contacts")]
    [InlineData("Edu/CourseInvite", "Course Invites")]
    [InlineData("Essentials/Email", "Emails")]
    [InlineData("Approvals/Approval", "Approvals")]
    [InlineData("Billing/Address", "Addresses")]
    [InlineData("Markdown", "Documents")]
    [InlineData(null, "Other")]
    public void ASectionIsNamedByTheCatalogElseByItsTypeInThePlural(string? nodeType, string expected)
        => IndexSections.Label(nodeType, null).Should().Be(expected);

    [Fact]
    public void TheCatalogLabelsAreTranslated()
        => IndexSections.Label("Markdown", "de").Should().Be("Dokumente");
}
