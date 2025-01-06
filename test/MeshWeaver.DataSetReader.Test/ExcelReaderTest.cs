using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.DataSetReader.Excel;
using MeshWeaver.DataStructures;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.DataSetReader.Test
{
    /// <summary>
    ///             .Add(MimeTypes.Csv,  (stream,options,_) => DataSetCsvSerializer.ReadAsync(stream,options))
    //.Add(MimeTypes.Xlsx, new ExcelDataSetReader().ReadAsync)
    //.Add(MimeTypes.Xls, new ExcelDataSetReaderOld().ReadAsync)
    /// </summary>
    public class ExcelReaderTest : DataSetReaderTestBase
    {
        private const string FilesFolder = @"FilesForTests/Excel/";
        (IDataSet DataSet, string Format) ReadFromStream(Stream stream) => new ExcelDataSetReader().Read(stream);
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
            var ret = ReadFromStream(stream);
            ret.Format.Should().Be("Test");
        }
    }
}
