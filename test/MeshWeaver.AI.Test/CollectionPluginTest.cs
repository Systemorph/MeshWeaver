using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FluentAssertions;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for CollectionPlugin functionality, specifically the GetFile method with Excel support
/// </summary>
public class CollectionPluginTest(ITestOutputHelper output) : HubTestBase(output), IAsyncLifetime
{
    private const string TestCollectionName = "test-collection";
    private const string TestExcelFileName = "test.xlsx";
    private const string TestTextFileName = "test.txt";
    private readonly string collectionBasePath = Path.Combine(Path.GetTempPath(), $"CollectionPluginTest_{Guid.NewGuid()}");

    /// <summary>
    /// Initialize the test
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Create directory for test files
        Directory.CreateDirectory(collectionBasePath);

        // Create test Excel file with empty cells at the start
        CreateTestExcelFile();

        // Create test text file
        await CreateTestTextFile();
    }    /// <summary>
         /// Dispose the test
         /// </summary>
    public override async ValueTask DisposeAsync()
    {
        // Clean up test files and directory
        if (Directory.Exists(collectionBasePath))
        {
            try
            {
                Directory.Delete(collectionBasePath, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Creates a test Excel file with multiple worksheets and empty cells at the start of rows
    /// </summary>
    private void CreateTestExcelFile()
    {
        using var wb = new XLWorkbook();

        // Create first worksheet with data including null cells at the start
        var ws1 = wb.Worksheets.Add("Sheet1");

        // Header row with empty cells at start
        ws1.Cell(1, 1).Value = ""; // Empty cell
        ws1.Cell(1, 2).Value = ""; // Empty cell
        ws1.Cell(1, 3).Value = "ID";
        ws1.Cell(1, 4).Value = "Name";
        ws1.Cell(1, 5).Value = "Value";

        // Data rows with empty cells
        ws1.Cell(2, 1).Value = ""; // Empty
        ws1.Cell(2, 2).Value = ""; // Empty
        ws1.Cell(2, 3).Value = "1";
        ws1.Cell(2, 4).Value = "Item A";
        ws1.Cell(2, 5).Value = "100";

        ws1.Cell(3, 1).Value = ""; // Empty
        ws1.Cell(3, 2).Value = ""; // Empty
        ws1.Cell(3, 3).Value = "2";
        ws1.Cell(3, 4).Value = ""; // Empty cell in middle
        ws1.Cell(3, 5).Value = "200";

        // Add more rows for testing row limiting
        for (int i = 4; i <= 30; i++)
        {
            ws1.Cell(i, 1).Value = "";
            ws1.Cell(i, 2).Value = "";
            ws1.Cell(i, 3).Value = i - 1;
            ws1.Cell(i, 4).Value = $"Item {(char)('A' + i - 2)}";
            ws1.Cell(i, 5).Value = i * 100;
        }

        // Create second worksheet with minimal data
        var ws2 = wb.Worksheets.Add("Sheet2");
        ws2.Cell(1, 1).Value = "Column1";
        ws2.Cell(1, 2).Value = "Column2";
        ws2.Cell(2, 1).Value = "Value1";
        ws2.Cell(2, 2).Value = "Value2";

        var filePath = Path.Combine(collectionBasePath!, TestExcelFileName);
        wb.SaveAs(filePath);
    }

    /// <summary>
    /// Creates a test text file with multiple lines
    /// </summary>
    private async Task CreateTestTextFile()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 50; i++)
        {
            sb.AppendLine($"Line {i}");
        }

        var filePath = Path.Combine(collectionBasePath!, TestTextFileName);
        await File.WriteAllTextAsync(filePath, sb.ToString());
    }

    /// <summary>
    /// Tests that GetFile preserves null values in Excel files with empty cells at the start of rows
    /// </summary>
    [Fact]
    public async Task GetFile_ExcelWithEmptyCellsAtStart_ShouldPreserveNulls()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // act
        var result = await plugin.GetFile(TestCollectionName, TestExcelFileName);

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("## Sheet: Sheet1");
        result.Should().Contain("## Sheet: Sheet2");

        // Verify markdown table structure with column headers
        result.Should().Contain("| Row | A | B | C | D | E |");

        // Verify that empty cells at the start show as empty in the table
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerLine = lines.FirstOrDefault(l => l.Contains("ID") && l.Contains("Name"));
        headerLine.Should().NotBeNull();
        headerLine.Should().Contain("| 1 |  |  | ID | Name | Value |");

        var secondDataLine = lines.FirstOrDefault(l => l.Contains("Item A"));
        secondDataLine.Should().NotBeNull();
        secondDataLine.Should().Contain("| 2 |  |  | 1 | Item A | 100 |");

        // Verify empty cell in the middle (row 3 has empty Name column)
        var thirdDataLine = lines.FirstOrDefault(l => l.Contains("| 3 |"));
        thirdDataLine.Should().NotBeNull();
        thirdDataLine.Should().Contain("| 3 |  |  | 2 |  | 200 |");
    }

    /// <summary>
    /// Tests that GetFile with numberOfRows parameter limits Excel file output
    /// </summary>
    [Fact]
    public async Task GetFile_ExcelWithNumberOfRows_ShouldLimitRows()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);
        const int rowLimit = 5;

        // act
        var result = await plugin.GetFile(TestCollectionName, TestExcelFileName, numberOfRows: rowLimit);

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("## Sheet: Sheet1");

        // Count the number of data rows in the markdown table (excluding header and separator)
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLines = lines
            .SkipWhile(l => !l.Contains("| Row |"))
            .Skip(2) // Skip header and separator
            .TakeWhile(l => l.StartsWith("|") && !l.Contains("## Sheet:"))
            .ToList();

        dataLines.Count.Should().Be(rowLimit);

        // Verify it still has the markdown table structure
        result.Should().Contain("| Row | A | B | C | D | E |");
    }

    /// <summary>
    /// Tests that GetFile with numberOfRows parameter limits text file output
    /// </summary>
    [Fact]
    public async Task GetFile_TextWithNumberOfRows_ShouldLimitRows()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);
        const int rowLimit = 10;

        // act
        var result = await plugin.GetFile(TestCollectionName, TestTextFileName, numberOfRows: rowLimit);

        // assert
        result.Should().NotBeNullOrEmpty();

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToArray();
        lines.Length.Should().Be(rowLimit);

        lines[0].Should().Be("Line 1");
        lines[9].Should().Be("Line 10");
    }

    /// <summary>
    /// Tests that GetFile without numberOfRows parameter reads entire text file
    /// </summary>
    [Fact]
    public async Task GetFile_TextWithoutNumberOfRows_ShouldReadEntireFile()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // act
        var result = await plugin.GetFile(TestCollectionName, TestTextFileName);

        // assert
        result.Should().NotBeNullOrEmpty();

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToArray();
        lines.Length.Should().Be(50);

        lines[0].Should().Be("Line 1");
        lines[49].Should().Be("Line 50");
    }

    /// <summary>
    /// Tests that GetFile without numberOfRows parameter reads entire Excel file
    /// </summary>
    [Fact]
    public async Task GetFile_ExcelWithoutNumberOfRows_ShouldReadEntireFile()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // act
        var result = await plugin.GetFile(TestCollectionName, TestExcelFileName);

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("## Sheet: Sheet1");
        result.Should().Contain("## Sheet: Sheet2");

        // Should have all 30 rows from Sheet1 in the markdown table
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sheet1DataLines = lines
            .SkipWhile(l => !l.Contains("## Sheet: Sheet1"))
            .SkipWhile(l => !l.Contains("| Row |"))
            .Skip(2) // Skip header and separator
            .TakeWhile(l => l.StartsWith("|") && !l.Contains("## Sheet:"))
            .ToList();

        sheet1DataLines.Count.Should().Be(30);
    }

    /// <summary>
    /// Tests that GetFile handles non-existent collection
    /// </summary>
    [Fact]
    public async Task GetFile_NonExistentCollection_ShouldReturnErrorMessage()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // act
        var result = await plugin.GetFile("non-existent-collection", "test.xlsx");

        // assert
        result.Should().Contain("Collection 'non-existent-collection' not found");
    }

    /// <summary>
    /// Tests that GetFile handles non-existent file
    /// </summary>
    [Fact]
    public async Task GetFile_NonExistentFile_ShouldReturnErrorMessage()
    {
        // arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // act
        var result = await plugin.GetFile(TestCollectionName, "non-existent.xlsx");

        // assert
        result.Should().Contain("File 'non-existent.xlsx' not found");
    }

    /// <summary>
    /// Configuration for test client
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddFileSystemContentCollection(TestCollectionName, _ => collectionBasePath);
    }
}
