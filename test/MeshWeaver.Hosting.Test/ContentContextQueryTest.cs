using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <c>is:content</c> — a surface saying what it is, instead of borrowing a name that happened to
/// filter the way it wanted.
///
/// <para>Every browsing surface used to pass <c>context:search</c>: the home screen, a node's
/// children list, the recent lists. None of them are the search box. They said it because that was
/// the one context whose filtering withheld registration nodes — and each of them then still needed
/// its own <c>-nodeType:…</c> patch on top, because a borrowed name does not say what you mean.</para>
///
/// <para>The registration/content split is the substance: the in-memory type, module and partition
/// declarations <c>AddMeshNodes</c> contributes exist so the platform knows a type exists. They are
/// what a create menu offers. They are not things a person browses, and they should never have been
/// listed next to somebody's actual spaces.</para>
/// </summary>
public class ContentContextQueryTest
{
    private static ParsedQuery Parse(string query) => new QueryParser().Parse(query);

    [Fact]
    public void Is_content_sets_the_flag_AND_implies_the_content_context()
    {
        var parsed = Parse("is:main is:content namespace:acme");

        parsed.IsContent.Should().BeTrue();
        parsed.IsMain.Should().BeTrue();
        // Implying the context is what keeps the EXISTING exclusion machinery working — per-node
        // ExcludeFromContext, and per-type when the marked node is a NodeType definition, pushed
        // into SQL by the database providers. Without it, dropping context:search from these
        // surfaces would have REVEALED the infrastructure nodes it was quietly filtering.
        parsed.Context.Should().Be(MeshContexts.Content);
    }

    [Fact]
    public void An_explicit_context_always_wins_over_the_implied_one()
    {
        Parse("is:content context:create").Context.Should().Be(MeshContexts.Create);
        Parse("is:content context:search").Context.Should().Be(MeshContexts.Search);
    }

    [Fact]
    public void A_query_that_does_not_say_is_content_is_untouched()
    {
        var parsed = Parse("nodeType:Markdown namespace:acme");

        parsed.IsContent.Should().NotBe(true);
        parsed.Context.Should().BeNull(
            "every internal query that names no context keeps its existing behaviour");
    }

    [Fact]
    public void Is_main_still_parses_on_its_own()
    {
        var parsed = Parse("is:main");
        parsed.IsMain.Should().BeTrue();
        parsed.IsContent.Should().NotBe(true);
    }

    [Fact]
    public void HideFrom_is_additive_and_idempotent_so_two_declarers_cannot_drop_each_other()
    {
        var node = new MeshNode("Thing").HideFrom(MeshContexts.Search);

        // A module marks it, then a deployment's own config marks it again for something else.
        // Neither may silently discard the other's.
        var both = node.HideFrom(MeshContexts.Content).HideFrom(MeshContexts.Search);

        both.ExcludeFromContext!.Should().HaveCount(2);
        both.ExcludeFromContext.Should().Contain(MeshContexts.Search);
        both.ExcludeFromContext.Should().Contain(MeshContexts.Content);
    }

    [Fact]
    public void HideEverywhere_covers_the_three_a_person_can_reach()
    {
        var hidden = new MeshNode("Secret").HideEverywhere().ExcludeFromContext!;

        hidden.Should().HaveCount(3);
        hidden.Should().Contain(MeshContexts.Search);
        hidden.Should().Contain(MeshContexts.Create);
        hidden.Should().Contain(MeshContexts.Content);
    }

    [Fact]
    public void HideFrom_with_nothing_to_add_leaves_the_node_alone()
    {
        var node = new MeshNode("Thing");
        node.HideFrom().ExcludeFromContext.Should().BeNull();
    }
}
