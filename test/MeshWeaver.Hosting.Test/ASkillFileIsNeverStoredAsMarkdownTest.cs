using System;
using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// fb-1fa1722b: a GitSync import on a host with no AI parsers stored ~32 package skills and the
/// package agents as <c>MarkdownContent</c> (front matter dropped). An importer must notice that a
/// <c>nodeType: Skill</c> / <c>Agent</c> file only reached the catch-all markdown parser, and must
/// pick up parsers a module contributes AFTER the service was constructed.
/// </summary>
public class ASkillFileIsNeverStoredAsMarkdownTest
{
    private const string SkillFile = "---\nnodeType: Skill\nname: Email\naction: pick\n---\nDo the email thing.\n";
    private const string PlainFile = "---\nname: Notes\n---\nJust a note.\n";
    private const string TypedMarkdownFile = "---\nnodeType: Documentation\nname: Doc\n---\nBody.\n";

    /// <summary>Stands in for the AI module's SkillFileParser: claims nodeType Skill, produces non-markdown content.</summary>
    private sealed class StubSkillParser : IFileFormatParser
    {
        public IReadOnlyList<string> SupportedExtensions { get; } = [".md"];

        public MeshNode? Parse(string filePath, string content, string relativePath) =>
            content.Contains("nodeType: Skill")
                ? new MeshNode("email", "Skill") { NodeType = "Skill", Content = new SkillStub("Do the email thing.") }
                : null;

        public string Serialize(MeshNode node) => "";
        public bool CanSerialize(MeshNode node) => false;
    }

    private sealed record SkillStub(string Instructions);

    private static MeshNode Parse(FileFormatParserRegistry registry, string content)
    {
        var node = registry.TryParse(".md", "Pkg/Skill/email.md", content, "Pkg/Skill/email.md");
        Assert.NotNull(node);
        return node!;
    }

    [Fact]
    public void With_the_skill_parser_registered_the_file_is_a_Skill_and_nothing_is_flagged()
    {
        var registry = new FileFormatParserRegistry(null, [new StubSkillParser()]);
        var node = Parse(registry, SkillFile);

        Assert.Equal("Skill", node.NodeType);
        Assert.IsType<SkillStub>(node.Content);
        Assert.Null(ParserBoundNodeTypes.Describe("Pkg/Skill/email.md", node));
    }

    [Fact]
    public void Without_the_skill_parser_the_skill_file_is_flagged_not_stored_as_markdown()
    {
        var registry = new FileFormatParserRegistry();
        var node = Parse(registry, SkillFile);

        // The silent degradation: typed Skill, content MarkdownContent.
        Assert.Equal("Skill", node.NodeType);
        Assert.IsType<MarkdownContent>(node.Content);
        var problem = ParserBoundNodeTypes.Describe("Pkg/Skill/email.md", node);
        Assert.NotNull(problem);
        Assert.Contains("Pkg/Skill/email.md", problem);
    }

    [Fact]
    public void Negative_control_plain_markdown_stays_Markdown_with_or_without_the_parser()
    {
        foreach (var registry in new[] { new FileFormatParserRegistry(), new FileFormatParserRegistry(null, [new StubSkillParser()]) })
        {
            var node = Parse(registry, PlainFile);
            Assert.Equal("Markdown", node.NodeType);
            Assert.IsType<MarkdownContent>(node.Content);
            Assert.Null(ParserBoundNodeTypes.Describe("Pkg/notes.md", node));
        }
    }

    [Fact]
    public void Negative_control_a_typed_markdown_node_that_is_not_parser_bound_is_not_flagged()
    {
        var node = Parse(new FileFormatParserRegistry(), TypedMarkdownFile);
        Assert.Equal("Documentation", node.NodeType);
        Assert.Null(ParserBoundNodeTypes.Describe("Pkg/doc.md", node));
    }

    [Fact]
    public void A_parser_contributed_after_the_service_was_built_is_used_on_the_next_import()
    {
        var contributed = new List<IFileFormatParser>();
        var options = new JsonSerializerOptions();
        var registries = new LateBoundParserRegistries(() => contributed.ToArray(), options, options);

        // Built (and used) before the AI module's services were visible: the skill degrades.
        var early = registries.Strict.TryParse(".md", "Pkg/Skill/email.md", SkillFile, "Pkg/Skill/email.md");
        Assert.IsType<MarkdownContent>(early!.Content);

        contributed.Add(new StubSkillParser());

        var late = registries.Strict.TryParse(".md", "Pkg/Skill/email.md", SkillFile, "Pkg/Skill/email.md");
        Assert.IsType<SkillStub>(late!.Content);
        Assert.Null(ParserBoundNodeTypes.Describe("Pkg/Skill/email.md", late));
    }

    [Fact]
    public void Negative_control_an_unchanged_contributed_set_reuses_the_registry()
    {
        var parser = new StubSkillParser();
        var options = new JsonSerializerOptions();
        var registries = new LateBoundParserRegistries(() => [parser], options, options);
        Assert.Same(registries.Normal, registries.Normal);
        Assert.Same(registries.Strict, registries.Strict);
    }
}
