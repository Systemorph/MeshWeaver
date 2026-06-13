using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="DataModelLayoutArea.BuildMermaidDiagram"/> and
/// <see cref="DataModelLayoutArea.RenderTypeDetails"/> are the reusable cores behind the
/// DataModel page AND the NodeType <c>$Model</c> view (Mermaid class diagram + per-type detail).
/// These tests pin the emitted Mermaid: class boxes, reference relationships, and click/detail
/// links built against an explicit <c>linkBase</c> (so the same cores serve <c>/DataModel</c> and
/// <c>/{nodeType}/$Model</c>).
/// </summary>
public class DataModelDiagramTest
{
    // Sample domain types with a reference relationship (Book -> Author).
    public record DmAuthor
    {
        public string Name { get; init; } = "";
    }

    public record DmBook
    {
        public string Title { get; init; } = "";
        public DmAuthor? Author { get; init; }
    }

    [Fact]
    public void BuildMermaidDiagram_EmitsClasses_Relationship_AndClickLinksUnderLinkBase()
    {
        var registry = new TestTypeRegistry()
            .WithType(typeof(DmBook), "DmBook")
            .WithType(typeof(DmAuthor), "DmAuthor");
        var book = registry.GetTypeDefinition(typeof(DmBook))!;
        var author = registry.GetTypeDefinition(typeof(DmAuthor))!;
        var all = new[] { book, author };

        var diagram = DataModelLayoutArea.BuildMermaidDiagram([book], all, "/Test/DataModel");

        diagram.Should().Contain("classDiagram");
        diagram.Should().Contain("class DmBook");
        diagram.Should().Contain("class DmAuthor",
            "the reference property's target type is pulled into the diagram group");
        diagram.Should().Contain("-- DmAuthor : Author",
            "a reference property becomes a relationship labelled by the property name");
        diagram.Should().Contain("click DmBook href \"/Test/DataModel/DmBook\"",
            "class nodes link under the supplied linkBase, not a hard-coded /DataModel path");
    }

    [Fact]
    public void BuildMermaidDiagram_IncludesPropertyNames()
    {
        var registry = new TestTypeRegistry().WithType(typeof(DmBook), "DmBook");
        var book = registry.GetTypeDefinition(typeof(DmBook))!;

        var diagram = DataModelLayoutArea.BuildMermaidDiagram([book], [book], "/X");

        diagram.Should().Contain("Title");
        diagram.Should().Contain("Author");
    }

    [Fact]
    public void RenderTypeDetails_ListsProperties_AndLinksDomainTypesUnderLinkBase()
    {
        var registry = new TestTypeRegistry()
            .WithType(typeof(DmBook), "DmBook")
            .WithType(typeof(DmAuthor), "DmAuthor");
        var book = registry.GetTypeDefinition(typeof(DmBook))!;
        var author = registry.GetTypeDefinition(typeof(DmAuthor))!;

        var control = DataModelLayoutArea.RenderTypeDetails(book, new[] { book, author }, "/Test/DataModel");

        var md = control.Should().BeOfType<MarkdownControl>().Which.Markdown?.ToString();
        md.Should().NotBeNull();
        md!.Should().Contain("DmBook");
        md.Should().Contain("## Properties");
        md.Should().Contain("Title");
        md.Should().Contain("/Test/DataModel/DmAuthor",
            "a property whose type is another domain type links to that type under linkBase");
    }
}
