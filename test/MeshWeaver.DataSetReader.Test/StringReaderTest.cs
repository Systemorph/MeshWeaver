using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.DataSetReader.Csv;
using MeshWeaver.DataStructures;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.DataSetReader.Test
{
    public class StringReaderTest : DataSetReaderTestBase
    {
        public StringReaderTest(ITestOutputHelper output)
            : base(output) { }

        private Task<(IDataSet DataSet, string Format)> ReadFromStream(
            Stream stream,
            DataSetReaderOptions? options = null
        ) => DataSetCsvSerializer.ReadAsync(stream, options ?? new() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" });

        [Fact]
        public async Task BasicStringImportTest()
        {
            const double doubleValue = 1.5;
            const decimal decimalValue = 2.5m;
            const int intValue = 3;
            var dateValue = new DateTime(2020, 02, 02).ToString("MM/dd/yyyy");
            const string stringValue = "Test";
            var content = $"{doubleValue},{decimalValue},{stringValue},{dateValue},{intValue}";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithOrder))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithOrder));
            table.Rows.Should().HaveCount(1);
            var row = table.Rows[0];

            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(dateValue);
            row[nameof(TestImportEntityWithOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DecimalProperty)]
                .Should()
                .Be(decimalValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.IntProperty)]
                .Should()
                .Be(intValue.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task BasicStringImportWithHeaderRowTest()
        {
            const double doubleValue = 1.5;
            const decimal decimalValue = 2.5m;
            const int intValue = 3;
            var dateValue = new DateTime(2020, 02, 02).ToString("MM/dd/yyyy");
            const string stringValue = "Test";
            var headers = new[]
            {
                nameof(TestImportEntityWithOrder.DoubleProperty),
                nameof(TestImportEntityWithOrder.IntProperty),
                nameof(TestImportEntityWithOrder.DecimalProperty),
                nameof(TestImportEntityWithOrder.StringProperty),
                nameof(TestImportEntityWithOrder.DateTimeProperty),
            };

            var content =
                @$"{string.Join(',', headers)}
{doubleValue},{decimalValue},{stringValue},{dateValue},{intValue}";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" }.WithEntityType(typeof(TestImportEntityWithOrder))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithOrder));
            table.Rows.Should().HaveCount(1);
            var row = table.Rows[0];

            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(dateValue);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)]
                .Should()
                .Be(intValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DecimalProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithOrder.IntProperty)]
                .Should()
                .Be(decimalValue.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task BasicStringImportTestWithCustomDelimiter()
        {
            const double doubleValue = 1.5;
            const decimal decimalValue = 2.5m;
            const int intValue = 3;
            var dateValue = new DateTime(2020, 02, 02).ToString("MM/dd/yyyy");
            const string stringValue = "Test";
            var content = $"{doubleValue};{decimalValue};{stringValue};{dateValue};{intValue}";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithOrder))
                    .WithDelimiter(';')
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithOrder));
            table.Rows.Should().HaveCount(1);
            var row = table.Rows[0];

            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(dateValue);
            row[nameof(TestImportEntityWithOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DecimalProperty)]
                .Should()
                .Be(decimalValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.IntProperty)]
                .Should()
                .Be(intValue.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task IncompleteSetOfValues()
        {
            const double doubleValue = 1.5;
            const decimal decimalValue = 2.5m;
            const string stringValue = "Test";
            var content = $"{doubleValue},{decimalValue},{stringValue}";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithOrder))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithOrder));
            table.Rows.Should().HaveCount(1);
            var row = table.Rows[0];

            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(null);
            row[nameof(TestImportEntityWithOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DecimalProperty)]
                .Should()
                .Be(decimalValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.IntProperty)].Should().Be(null);
        }

        [Fact]
        public async Task BasicStringImportTestWithEmptyValues()
        {
            const double doubleValue = 1.5;
            const decimal decimalValue = 2.5m;
            const int intValue = 3;
            var dateValue = new DateTime(2020, 02, 02).ToString("MM/dd/yyyy");
            const string stringValue = "Test";
            var content =
                @$"{doubleValue},{decimalValue},{stringValue},{dateValue},{intValue}
,,{stringValue},,";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithOrder))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithOrder));
            table.Rows.Should().HaveCount(2);

            var row = table.Rows[0];
            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(dateValue);
            row[nameof(TestImportEntityWithOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DecimalProperty)]
                .Should()
                .Be(decimalValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.IntProperty)]
                .Should()
                .Be(intValue.ToString(CultureInfo.InvariantCulture));

            row = table.Rows[1];
            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(null);
            row[nameof(TestImportEntityWithOrder.DoubleProperty)].Should().Be(null);
            row[nameof(TestImportEntityWithOrder.DecimalProperty)].Should().Be(null);
            row[nameof(TestImportEntityWithOrder.IntProperty)].Should().Be(null);
        }

        [Fact]
        public async Task MultipleRecordsWithMultilineStrings()
        {
            const double doubleValue = 1.5;
            const decimal decimalValue = 2.5m;
            const int intValue = 3;
            var dateValue = new DateTime(2020, 02, 02).ToString("MM/dd/yyyy");
            const string stringValue1 =
                @"some,
long,
string1,";
            const string stringValue2 =
                @"some,
long,
string2,";
            var content =
                @$"
{doubleValue},{decimalValue},""{stringValue1}"",{dateValue},{intValue}

, ,""{stringValue2}"", ,";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithOrder), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithOrder))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithOrder));
            table.Rows.Should().HaveCount(2);

            var row = table.Rows[0];
            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue1);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(dateValue);
            row[nameof(TestImportEntityWithOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.DecimalProperty)]
                .Should()
                .Be(decimalValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithOrder.IntProperty)]
                .Should()
                .Be(intValue.ToString(CultureInfo.InvariantCulture));

            row = table.Rows[1];
            row[nameof(TestImportEntityWithOrder.StringProperty)].Should().Be(stringValue2);
            row[nameof(TestImportEntityWithOrder.DateTimeProperty)].Should().Be(" ");
            row[nameof(TestImportEntityWithOrder.DoubleProperty)].Should().Be(null);
            row[nameof(TestImportEntityWithOrder.DecimalProperty)].Should().Be(" ");
            row[nameof(TestImportEntityWithOrder.IntProperty)].Should().Be(null);
        }

        [Fact]
        public async Task EntityWithListTest()
        {
            const double doubleValue = 1.5;
            const string stringValue = "Test";
            var content =
                @$"{doubleValue},1, 2 ,3,,{stringValue}
{doubleValue},3, 4 ,5 ,6,{stringValue}
{doubleValue},,,,,{stringValue}";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithListsAndOrder), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithListsAndOrder))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithListsAndOrder));
            table.Rows.Should().HaveCount(3);

            var row = table.Rows[0];
            row[nameof(TestImportEntityWithListsAndOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "0"].Should().Be("1");
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "1"].Should().Be(" 2 ");
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "2"].Should().Be("3");
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "3"].Should().Be(null);
            row[nameof(TestImportEntityWithListsAndOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));

            row = table.Rows[1];
            row[nameof(TestImportEntityWithListsAndOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "0"].Should().Be("3");
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "1"].Should().Be(" 4 ");
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "2"].Should().Be("5 ");
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "3"].Should().Be("6");
            row[nameof(TestImportEntityWithListsAndOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));

            row = table.Rows[2];
            row[nameof(TestImportEntityWithListsAndOrder.StringProperty)].Should().Be(stringValue);
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "0"].Should().Be(null);
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "1"].Should().Be(null);
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "2"].Should().Be(null);
            row[nameof(TestImportEntityWithListsAndOrder.ListOfIntegers) + "3"].Should().Be(null);
            row[nameof(TestImportEntityWithListsAndOrder.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task EntityWithUnspecifiedLengthForList()
        {
            const double doubleValue = 1.5;
            const string stringValue = "Test";
            var content = @$"{doubleValue},1,2,3,,{stringValue}";
            var stream = GetStreamFromString(content);

            var ret = await ReadFromStream(
                stream,
                new DataSetReaderOptions() { EntityType = typeof(TestImportEntityWithListWithoutLength), ContentType = "text/csv" }
                    .WithHeaderRow(false)
                    .WithEntityType(typeof(TestImportEntityWithListWithoutLength))
            );

            ret.DataSet.Tables.Should().HaveCount(1);
            var table = ret.DataSet.Tables[0];
            table.TableName.Should().Be(nameof(TestImportEntityWithListWithoutLength));
            table.Rows.Should().HaveCount(1);
            table
                .Columns.Where(c =>
                    c.ColumnName.Contains(
                        nameof(TestImportEntityWithListWithoutLength.ListOfIntegers)
                    )
                )
                .Should()
                .BeEmpty();

            var row = table.Rows[0];
            row[nameof(TestImportEntityWithListWithoutLength.DoubleProperty)]
                .Should()
                .Be(doubleValue.ToString(CultureInfo.InvariantCulture));
            row[nameof(TestImportEntityWithListWithoutLength.StringProperty)].Should().Be("1");
        }

        public Stream GetStreamFromString(string content) =>
            new MemoryStream(Encoding.ASCII.GetBytes(content));
    }
}
