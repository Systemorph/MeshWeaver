using DocSharp.Docx;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Transforms .docx documents to Markdown using DocSharp.Docx.
/// Registered as IContentTransformer via DI.
/// </summary>
public class DocSharpContentTransformer : IContentTransformer
{
    private static readonly HashSet<string> Extensions = [".docx"];

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default)
    {
        var converter = new DocxToMarkdownConverter();
        var markdown = converter.ConvertToString(input);
        return Task.FromResult(markdown);
    }
}
