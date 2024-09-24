using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Layout.Markdown;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Formatting;

namespace MeshWeaver.Ai.Index;

public static class MarkdownIndexer
{
    public static List<string> SplitMarkdownParagraphs(this IMessageHub hub, string markdownText, int maxChunkSize)
    {
        var pipeline = new MarkdownPipelineBuilder().Use(new LayoutAreaMarkdownExtension(hub)).Build();

        var document = Markdown.Parse(markdownText, pipeline);
        var chunks = new List<string>();
        var currentChunk = new List<string>();

        foreach (var block in document)
        {
            if (block is ParagraphBlock paragraph)
            {
                var paragraphText = paragraph.Inline.FirstChild.ToString();
                if (currentChunk.Sum(p => p.Length) + paragraphText.Length > maxChunkSize)
                {
                    chunks.Add(string.Join("\n\n", currentChunk));
                    currentChunk.Clear();
                }
                currentChunk.Add(paragraphText);
            }
        }

        if (currentChunk.Any())
        {
            chunks.Add(string.Join("\n\n", currentChunk));
        }

        return chunks;
    }

    private const string IndexName = "articles";
    private static void CreateIndex(SearchIndexClient searchIndexClient)
    {
        var fieldBuilder = new FieldBuilder();
        var searchFields = fieldBuilder.Build(typeof(MeshArticle));

        var definition = new SearchIndex(IndexName)
        {
            Fields = searchFields
        };

        searchIndexClient.CreateIndex(definition);
        Console.WriteLine("Index created");
    }
}
