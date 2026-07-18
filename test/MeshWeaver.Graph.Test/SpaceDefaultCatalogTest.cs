#pragma warning disable CS1591

using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Covers the default-catalog gate behind the Space Overview (<see cref="SpaceLayoutAreas.BuildSpaceView"/>) —
/// the second half of the issue #502 fix. Commit <c>e03f20fac</c> removed the unconditional
/// <c>BuildNavigation</c> catalog section, leaving spaces whose body carries no catalog embed with NO
/// catalog at all (Case 1: a body that predates the embed, was authored without it, or is a
/// markdown-backed Doc/Skill root). The catalog is now appended as a default bottom "Contents" section
/// ONLY when the effective body does not already embed one — so a template/custom body that carries its
/// own <c>@@("area:Search")</c> never double-renders (the reason the hardcoded section was removed).
/// </summary>
public class SpaceDefaultCatalogTest
{
    // ── EffectiveBodySource: the string BuildBodyContent actually renders (same priority) ──────────

    [Fact]
    public void EffectiveBodySource_PrefersPreRenderedHtml()
    {
        var node = new MeshNode("Acme", "") { PreRenderedHtml = "<p>prerendered</p>" };
        var space = new Space { Body = "space body" };
        SpaceLayoutAreas.EffectiveBodySource(space, node).Should().Be("<p>prerendered</p>");
    }

    [Fact]
    public void EffectiveBodySource_FallsBackToSpaceBody_ThenWelcome()
    {
        var node = new MeshNode("Acme", "");
        SpaceLayoutAreas.EffectiveBodySource(new Space { Body = "space body" }, node)
            .Should().Be("space body");
        // No body → the default welcome template (which itself embeds the catalog).
        SpaceLayoutAreas.EffectiveBodySource(new Space(), node)
            .Should().Be(SpaceNodeType.WelcomeMarkdown);
    }

    // ── BodyEmbedsCatalog: whether the body already shows a Search catalog (⇒ skip the default) ────

    [Theory]
    // The shipped template forms — MUST be detected so the default catalog is NOT double-rendered.
    [InlineData("## Contents\n\n@@(\"area/Search\")")]
    [InlineData("## Contents\n\n@@(\"area:Search\")")]
    [InlineData("Intro\n\n@@Search")]
    [InlineData("@@area/Search")]
    [InlineData("@@/Acme/area/Search")]
    [InlineData("@@(\"area:Search?groupBy=type\")")]
    // Pre-rendered HTML form (a markdown-backed body whose catalog embed already rendered to a div).
    [InlineData("<div class='layout-area' data-address='Acme' data-area='Search'></div>")]
    // The default welcome template carries the embed, so it counts as embedding the catalog.
    public void BodyEmbedsCatalog_DetectsCatalogEmbeds(string body)
        => SpaceLayoutAreas.BodyEmbedsCatalog(body).Should().BeTrue();

    [Fact]
    public void BodyEmbedsCatalog_TrueForDefaultWelcomeTemplate()
        => SpaceLayoutAreas.BodyEmbedsCatalog(SpaceNodeType.WelcomeMarkdown).Should().BeTrue(
            "the default welcome body embeds @@(\"area/Search\"), so the default bottom catalog must be skipped");

    [Theory]
    // A plain body with no catalog embed → the default catalog SHOULD be appended (issue #502, Case 1).
    [InlineData("")]
    [InlineData("# My Space\n\nJust some prose and a [link](/Doc).")]
    [InlineData("A table:\n\n| a | b |\n|---|---|\n| 1 | 2 |")]
    // An embed of a DIFFERENT area is not the catalog — the default catalog is still appended.
    [InlineData("@@(\"area/Overview\")")]
    [InlineData("@@Events")]
    public void BodyEmbedsCatalog_FalseWhenNoCatalog(string body)
        => SpaceLayoutAreas.BodyEmbedsCatalog(body).Should().BeFalse();
}
