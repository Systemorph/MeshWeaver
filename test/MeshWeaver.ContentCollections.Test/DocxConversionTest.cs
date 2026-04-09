using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Tests for docx → markdown conversion via IContentTransformer,
/// content autocomplete for document files, and agent content access.
/// </summary>
public class DocxConversionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _contentBasePath = Path.Combine(AppContext.BaseDirectory, "Files", "DocxTest");

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(_contentBasePath);
        CreateTestDocx(Path.Combine(_contentBasePath, "sample.docx"), "Hello World", "This is a test document.");
        // Also create a plain text file for comparison
        File.WriteAllText(Path.Combine(_contentBasePath, "readme.md"), "# Readme\nSome text.");

        return base.ConfigureClient(configuration)
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = _contentBasePath,
                Settings = new Dictionary<string, string>
                {
                    ["BasePath"] = _contentBasePath
                }
            });
    }

    [Fact]
    public async Task DocSharpContentTransformer_Converts_Docx_To_Markdown()
    {
        // Arrange
        var transformer = new DocSharpContentTransformer();
        transformer.SupportedExtensions.Should().Contain(".docx");

        var docxPath = Path.Combine(_contentBasePath, "sample.docx");
        await using var stream = File.OpenRead(docxPath);

        // Act
        var markdown = await transformer.TransformToMarkdownAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Converted markdown:\n{markdown}");
        markdown.Should().NotBeNullOrWhiteSpace();
        markdown.Should().Contain("Hello World");
        markdown.Should().Contain("test document");
    }

    [Fact]
    public async Task FileContentProvider_Auto_Converts_Docx()
    {
        // Arrange
        var hub = GetClient();
        var fileContentProvider = hub.ServiceProvider.GetRequiredService<IFileContentProvider>();

        // Act — requesting a .docx file should auto-convert to markdown
        var result = await fileContentProvider.GetFileContentAsync("content", "sample.docx", ct: TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Content result:\n{result.Content}");
        result.Success.Should().BeTrue();
        result.Content.Should().NotBeNullOrWhiteSpace();
        result.Content.Should().Contain("Hello World");
        result.Content.Should().Contain("test document");
    }

    [Fact]
    public async Task FileContentProvider_Returns_PlainText_For_Md()
    {
        // Arrange
        var hub = GetClient();
        var fileContentProvider = hub.ServiceProvider.GetRequiredService<IFileContentProvider>();

        // Act — requesting a .md file should return as-is
        var result = await fileContentProvider.GetFileContentAsync("content", "readme.md", ct: TestContext.Current.CancellationToken);

        // Assert
        result.Success.Should().BeTrue();
        result.Content.Should().Contain("# Readme");
        result.Content.Should().Contain("Some text.");
    }

    [Fact]
    public async Task ContentAutocomplete_Filters_And_Scores_By_Query()
    {
        // Arrange
        var hub = GetClient();
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var contentProvider = providers
            .OfType<ContentCollections.Completion.ContentAutocompleteProvider>()
            .FirstOrDefault();
        contentProvider.Should().NotBeNull("ContentAutocompleteProvider should be registered");

        // Act — query "sample" should match sample.docx but NOT readme.md
        var items = new List<AutocompleteItem>();
        await foreach (var item in contentProvider!.GetItemsAsync("sample", ct: TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        // Assert — only matching files returned
        Output.WriteLine($"Autocomplete items for 'sample': {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  [{item.Priority}] {item.Label} => {item.InsertText}");

        items.Should().Contain(i => i.Label == "sample.docx");
        items.Should().NotContain(i => i.Label == "readme.md", "non-matching files should be filtered out");

        var docxItem = items.First(i => i.Label == "sample.docx");
        docxItem.InsertText.Should().Contain("content:");
        docxItem.InsertText.Should().NotContain("content:content/", "should not have duplicate content prefix");
        docxItem.Description.Should().Contain("converts to markdown");
        docxItem.Kind.Should().Be(AutocompleteKind.File);
        docxItem.Priority.Should().BeGreaterThan(2000, "prefix match should get high priority");
    }

    [Fact]
    public async Task ContentAutocomplete_ExactMatch_Gets_Highest_Priority()
    {
        // Arrange
        var hub = GetClient();
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var contentProvider = providers
            .OfType<ContentCollections.Completion.ContentAutocompleteProvider>()
            .FirstOrDefault();
        contentProvider.Should().NotBeNull();

        // Act — exact name match
        var items = new List<AutocompleteItem>();
        await foreach (var item in contentProvider!.GetItemsAsync("sample.docx", ct: TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        // Assert — exact match should score 3000
        var docxItem = items.First(i => i.Label == "sample.docx");
        docxItem.Priority.Should().Be(3000, "exact name match should get highest score");
    }

    [Fact]
    public async Task ContentAutocomplete_Wraps_Spaces_In_Quotes()
    {
        // Arrange — create a file with spaces in the name
        File.WriteAllText(Path.Combine(_contentBasePath, "my document.docx"), "");
        // Create a valid docx instead
        CreateTestDocx(Path.Combine(_contentBasePath, "my document.docx"), "Spaced Doc", "Content");

        var hub = GetClient();
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var contentProvider = providers
            .OfType<ContentCollections.Completion.ContentAutocompleteProvider>()
            .FirstOrDefault();

        // Act
        var items = new List<AutocompleteItem>();
        await foreach (var item in contentProvider!.GetItemsAsync("my doc", ct: TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        // Assert
        Output.WriteLine($"Items for 'my doc': {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} => {item.InsertText}");

        var spacedItem = items.FirstOrDefault(i => i.Label == "my document.docx");
        spacedItem.Should().NotBeNull("file with spaces should be found");
        spacedItem!.InsertText.Should().StartWith("\"", "paths with spaces should be quoted");
        spacedItem.InsertText.Should().EndWith("\" ", "paths with spaces should be quoted with trailing space");
    }

    [Fact]
    public async Task GetDataRequest_Content_Prefix_Returns_Markdown_For_Docx()
    {
        // Arrange — the unified path content:content/sample.docx should auto-convert
        var hub = GetClient();
        var request = new GetDataRequest(new UnifiedReference("content:content/sample.docx"));
        var delivery = hub.Post(request, o => o.WithTarget(hub.Address))!;

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token);

        // Assert
        response.Should().BeAssignableTo<IMessageDelivery<GetDataResponse>>();
        var dataResponse = ((IMessageDelivery<GetDataResponse>)response).Message;
        Output.WriteLine($"Response data: {dataResponse.Data}");
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();
        dataResponse.Data!.ToString().Should().Contain("Hello World");
    }

    /// <summary>
    /// Creates a minimal .docx file with a heading and body paragraph.
    /// </summary>
    private static void CreateTestDocx(string path, string heading, string bodyText)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        // Add heading
        var headingParagraph = body.AppendChild(new Paragraph());
        var headingRun = headingParagraph.AppendChild(new Run());
        headingRun.AppendChild(new Text(heading));
        headingParagraph.ParagraphProperties = new ParagraphProperties(
            new ParagraphStyleId { Val = "Heading1" });

        // Add body text
        var bodyParagraph = body.AppendChild(new Paragraph());
        var bodyRun = bodyParagraph.AppendChild(new Run());
        bodyRun.AppendChild(new Text(bodyText));
    }
}
