using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// The node types whose <c>.md</c> files are meaningful ONLY when a module-contributed
/// <see cref="IFileFormatParser"/> reads them (the AI engine's <c>SkillFileParser</c> /
/// <c>AgentFileParser</c>).
///
/// <para>🚨 Why this exists. <see cref="MarkdownFileParser"/> accepts every <c>.md</c>, so on a host
/// whose service provider carries no AI parsers it types a skill file <c>nodeType: Skill</c> and
/// fills it with <see cref="MarkdownContent"/> — the front matter's <c>action</c>, <c>invocation</c>,
/// <c>requires</c>, … are dropped, nothing throws, and the node LOOKS right. An import then stores it
/// (2026-11-17: ~32 package skills and the package agents on the public instance, taking /email and
/// /feedback out of service; feedback fb-1fa1722b). An importer that holds such a result has NOT
/// parsed the file, and must say so instead of writing it.</para>
/// </summary>
public static class ParserBoundNodeTypes
{
    private static readonly string[] Types = ["Skill", "Agent"];

    /// <summary>True when <paramref name="nodeType"/> is one whose file format a contributed parser owns.</summary>
    public static bool IsParserBound(string? nodeType) =>
        nodeType is not null && Types.Contains(nodeType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A problem line when <paramref name="parsed"/> is a parser-bound type that fell through to the
    /// catch-all markdown parser (its content is still <see cref="MarkdownContent"/>), else null.
    /// </summary>
    /// <param name="path">The file's path, named in the message.</param>
    /// <param name="parsed">What the registry produced for the file.</param>
    public static string? Describe(string path, MeshNode parsed) =>
        IsParserBound(parsed.NodeType) && parsed.Content is MarkdownContent
            ? $"{path}: declares nodeType '{parsed.NodeType}' but was read as plain markdown — the parser for that type "
              + "(the AI module's file parser) is not registered on this host, so its front matter would be dropped. "
              + "Import it on a host where the AI module is active."
            : null;
}
