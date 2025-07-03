using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using FluentAssertions;
using MeshWeaver.DataSetReader.Csv;
using MeshWeaver.DataStructures;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.DataSetReader.Test
{
    public class CsvReaderTest : DataSetReaderTestBase
    {
        private const string CsvFilesWithFormat = @"FilesForTests/Csv/";
        private const string CsvFilesWithComplexCases = @$"{CsvFilesWithFormat}ComplexCases/";
        private const string CsvFilesWithComplexCasesDifferentFormat = @$"{CsvFilesWithComplexCases}/FormatCases/";

        private const string Cashflow = nameof(Cashflow);
        private const string CashflowWrapper = nameof(CashflowWrapper);
        private const string ResultsKey = nameof(ResultsKey);
        private const string NamedSubject = nameof(NamedSubject);
        private const string ReportingNodeByCurrency = nameof(ReportingNodeByCurrency);

        private Task<(IDataSet DataSet, string Format)> ReadFromStream(Stream stream, DataSetReaderOptions options = null) =>
            DataSetCsvSerializer.ReadAsync(stream, options ?? new() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" });

        public CsvReaderTest(ITestOutputHelper output)
            : base(output)
        {
        }

        [Theory]
        [InlineData("OneEmptyTable.csv")]
        [InlineData("OneEmptyTableWithCommas.csv")]
        public async Task ReadOneEmptyTableInDataSet(string fileName)
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCases}{fileName}");
            var ret = await ReadFromStream(stream);

            ret.DataSet.Tables.Count.Should().Be(1);
            ret.DataSet.Tables.First().TableName.Should().Be(Cashflow);
            ret.DataSet.Tables.First().Columns.Count.Should().Be(0);
            ret.DataSet.Tables.First().Rows.Count.Should().Be(0);
        }

        [Theory]
        [InlineData("SeveralEmptyTables.csv")]
        [InlineData("SeveralEmptyTablesWithCommas.csv")]
        public async Task ReadManyEmptyTableInDataSet(string fileName)
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCases}{fileName}");
            var ret = await ReadFromStream(stream);

            ret.DataSet.Tables.Count.Should().Be(2);
            ret.DataSet.Tables[Cashflow].Columns.Count().Should().Be(0);
            ret.DataSet.Tables[Cashflow].Rows.Count().Should().Be(0);

            ret.DataSet.Tables[ResultsKey].Columns.Count().Should().Be(0);
            ret.DataSet.Tables[ResultsKey].Rows.Count().Should().Be(0);
        }

        [Fact]
        public async Task ReadSeveralFilledTableWithCommasInDataSet()
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCases}SeveralFilledTablesWithCommas.csv");
            var ret = await ReadFromStream(stream);

            ret.DataSet.Tables.Count.Should().Be(2);
            ret.DataSet.Tables[Cashflow].Columns.Count().Should().Be(4);
            ret.DataSet.Tables[Cashflow].Rows.Count().Should().Be(3);

            ret.DataSet.Tables[ResultsKey].Columns.Count().Should().Be(3);
            ret.DataSet.Tables[ResultsKey].Rows.Count().Should().Be(2);
        }

        [Fact]
        public async Task ReadTableWithMultilineValues()
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCases}MultilineValues.csv");
            var ret = await ReadFromStream(stream);

            ret.DataSet.Tables.Count.Should().Be(1);
            var table = ret.DataSet.Tables[ReportingNodeByCurrency];
            table.Columns.Count().Should().Be(3);
            table.Rows.Count().Should().Be(6);

            table.Rows[0][0].Should().Be("SimpleCase");
            table.Rows[0][1].Should().Be("Simple case");
            table.Rows[0][2].Should().Be("Some \"currency\"");
            table.Rows[1][0].Should().Be("MultilineCase");
            table.Rows[1][1].Should().Be("Multiline case");
            table.Rows[1][2].Should().Be($"Case {Environment.NewLine}with \"multiple{Environment.NewLine}lines\"");
            table.Rows[2][0].Should().Be("MultilineCaseWithSingleQuoteOnLine");
            table.Rows[2][1].Should().Be("Multiline case with single quote on line");
            table.Rows[2][2].Should().Be($"{Environment.NewLine}Multiline{Environment.NewLine}\"\"case\"\"{Environment.NewLine}");
            table.Rows[3][0].Should().Be("TwoMultilineValuesOnTheSameLine");
            table.Rows[3][1].Should().Be($"\"Multiline {Environment.NewLine}display\" name");
            table.Rows[3][2].Should().Be($"Multiline {Environment.NewLine}values");
            table.Rows[4][0].Should().Be("TwoMultilineValuesOnTheSameLineWithCommasInsideMultilines");
            table.Rows[4][1].Should().Be($"\"Multiline,{Environment.NewLine}display\" name");
            table.Rows[4][2].Should().Be($"Multiline,{Environment.NewLine}values");
            table.Rows[5][0].Should().Be("MultilineCaseWithSingleQuoteOnLine");
            table.Rows[5][1].Should().Be("Multiline case with single quote on line");
            table.Rows[5][2].Should().Be($"{Environment.NewLine}Multiline,value{Environment.NewLine}\"\"case,draft\",logic\"{Environment.NewLine}");
        }

        [Fact]
        public async Task ReadSeveralFilledTableAndFewEmptyTablesWithCommasInData()
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCases}SeveralFilledAndFewEmptyTablesWithCommas.csv");
            var ret = await ReadFromStream(stream);

            ret.DataSet.Tables.Count.Should().Be(4);
            ret.DataSet.Tables[NamedSubject].Columns.Count().Should().Be(0);
            ret.DataSet.Tables[NamedSubject].Rows.Count().Should().Be(0);

            ret.DataSet.Tables[Cashflow].Columns.Count().Should().Be(4);
            ret.DataSet.Tables[Cashflow].Rows.Count().Should().Be(3);

            ret.DataSet.Tables[ResultsKey].Columns.Count().Should().Be(3);
            ret.DataSet.Tables[ResultsKey].Rows.Count().Should().Be(2);

            ret.DataSet.Tables[CashflowWrapper].Columns.Count().Should().Be(0);
            ret.DataSet.Tables[CashflowWrapper].Rows.Count().Should().Be(0);
        }

        [Theory]
        [InlineData("ComplexCaseDefaultFormat.csv")]
        [InlineData("ComplexCaseEmptyFormat.csv")]
        [InlineData("ComplexCaseNoFormat.csv")]
        [InlineData("ComplexCaseWrongFormat.csv")]
        public async Task ReadComplexCaseWithDifferentFormat(string fileName)
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCasesDifferentFormat}{fileName}");
            var ret = await ReadFromStream(stream);

            ret.DataSet.Tables.Count.Should().Be(2);
            ret.DataSet.Tables[NamedSubject].Columns.Count().Should().Be(0);
            ret.DataSet.Tables[NamedSubject].Rows.Count().Should().Be(0);

            ret.DataSet.Tables[Cashflow].Columns.Count().Should().Be(4);
            ret.DataSet.Tables[Cashflow].Rows.Count().Should().Be(3);
        }

        [Theory]
        [InlineData("CustomFormat.csv", "CustomFormat")]
        [InlineData("EmptyFormat.csv", "")]
        [InlineData("SpacesBeforeFormat.csv", "SpacesBeforeFormat")]
        public async Task ReadFormat(string fileName, string format)
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithFormat}{fileName}");
            var ret = await ReadFromStream(stream);

            ret.Format.Should().Be(format);
            var table = ret.DataSet.Tables.Should().ContainSingle().Which;
            table.TableName.Should().Be("Subject");
            table.Rows.Should().HaveCount(2);
        }

        [Fact]
        public async Task BadDataTest()
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{CsvFilesWithComplexCases}BadData.csv");
            Func<Task> act = async () => await ReadFromStream(stream);
            (await act.Should().ThrowAsync<BadDataException>()).Where(x => x.Message.Contains("\"Here opening quote is started but not finished") &&
                                                                           x.Message.Contains("\"ThisQuote will be treated as closing for first line"));
        }
    }
}
