using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;

namespace MeshWeaver.GitSync;

/// <summary>
/// The import's parser registries, built from the parsers a host contributes AT THE TIME OF USE.
///
/// <para>🚨 The sync service is constructed once, possibly before a module's services are visible to
/// its provider. A registry captured then lacks the module's parsers (the AI module's Skill/Agent
/// file parsers) for the life of the process, so every import falls through to the catch-all
/// markdown parser and stores Skill/Agent files as <c>MarkdownContent</c> with their front matter
/// dropped (fb-1fa1722b). Rebuilt whenever the contributed set changes.</para>
/// </summary>
internal sealed class LateBoundParserRegistries(
    Func<IReadOnlyList<IFileFormatParser>> contributed, JsonSerializerOptions normal, JsonSerializerOptions strict)
{
    private readonly object gate = new();
    private IReadOnlyList<IFileFormatParser>? builtFor;
    private FileFormatParserRegistry? normalRegistry;
    private FileFormatParserRegistry? strictRegistry;

    public FileFormatParserRegistry Normal => Current().Normal;
    public FileFormatParserRegistry Strict => Current().Strict;

    private (FileFormatParserRegistry Normal, FileFormatParserRegistry Strict) Current()
    {
        var now = contributed();
        lock (gate)
        {
            if (normalRegistry is null || strictRegistry is null || builtFor is null || !builtFor.SequenceEqual(now))
            {
                normalRegistry = new FileFormatParserRegistry(normal, now);
                strictRegistry = new FileFormatParserRegistry(strict, now);
                builtFor = now;
            }
            return (normalRegistry, strictRegistry);
        }
    }
}
