using DocSharp.Docx;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Transforms .docx documents to Markdown using DocSharp.Docx.
/// Registered as IContentTransformer via DI.
/// </summary>
public class DocSharpContentTransformer : IContentTransformer
{
    private static readonly HashSet<string> Extensions = [".docx"];

    /// <summary>The file extensions this transformer handles (<c>.docx</c>).</summary>
    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    /// <summary>Converts a <c>.docx</c> document stream into Markdown text.</summary>
    /// <param name="input">The document stream to convert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The document rendered as Markdown.</returns>
    public Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default)
    {
        var converter = new DocxToMarkdownConverter();
        var markdown = converter.ConvertToString(input);
        return Task.FromResult(markdown);
    }
}
