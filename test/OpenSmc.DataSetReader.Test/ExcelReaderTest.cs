using FluentAssertions;
using OpenSmc.DataSetReader.Excel.Utils;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.DataSetReader.Test
{
    public class ExcelReaderTest : DataSetReaderTestBase
    {
        private const string FilesFolder = @"FilesForTests/Excel/";

        public ExcelReaderTest(ITestOutputHelper output)
            : base(output)
        {
        }

        [Theory]
        [InlineData("Child.xlsx")]
        [InlineData("CustomDataFormat.xlsx")]
        public async Task MessageWarningShouldBeRegistered(string fileName)
        {
            var stream = await FileStorageService.GetStreamFromFilePath($"{FilesFolder}{fileName}");
            var ret = await DataSetReaderVariable.ReadFromStream(stream).WithContentType(ExcelExtensions.Excel10).ExecuteAsync();
            ret.Format.Should().Be("Test");
        }
    }
}
