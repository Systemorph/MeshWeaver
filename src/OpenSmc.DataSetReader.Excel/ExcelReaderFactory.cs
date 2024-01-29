using OpenSmc.DataSetReader.Excel.BinaryFormat;
using OpenSmc.DataSetReader.Excel.OpenXmlFormat;
using OpenSmc.DataSetReader.Excel.Utils;

namespace OpenSmc.DataSetReader.Excel
{
	public class ExcelReaderFactory : IExcelReaderFactory
	{

		/// <summary>
		/// Creates an instance of <see cref="ExcelBinaryReader"/>
		/// </summary>
		/// <param name="fileStream">The file stream.</param>
		/// <returns></returns>
		public IExcelDataReader CreateBinaryReader(Stream fileStream)
		{
			IExcelDataReader reader = new ExcelBinaryReader();
			reader.Initialize(fileStream);

			return reader;
		}

		/// <summary>
		/// Creates an instance of <see cref="ExcelBinaryReader"/>
		/// </summary>
		/// <param name="fileStream">The file stream.</param>
		/// <param name="option"></param>
		/// <returns></returns>
		public IExcelDataReader CreateBinaryReader(Stream fileStream, ReadOption option)
		{
			IExcelDataReader reader = new ExcelBinaryReader(option);
			reader.Initialize(fileStream);

			return reader;
		}

		/// <summary>
		/// Creates an instance of <see cref="ExcelBinaryReader"/>
		/// </summary>
		/// <param name="fileStream">The file stream.</param>
		/// <param name="convertOADate"></param>
		/// <returns></returns>
		public IExcelDataReader CreateBinaryReader(Stream fileStream, bool convertOADate)
		{
			IExcelDataReader reader = CreateBinaryReader(fileStream);
			((ExcelBinaryReader)reader).ConvertOaDate = convertOADate;

			return reader;
		}

		/// <summary>
		/// Creates an instance of <see cref="ExcelBinaryReader"/>
		/// </summary>
		/// <param name="fileStream">The file stream.</param>
		/// <param name="convertOADate"></param>
		/// <param name="readOption"></param>
		/// <returns></returns>
		public IExcelDataReader CreateBinaryReader(Stream fileStream, bool convertOADate, ReadOption readOption)
		{
			IExcelDataReader reader = CreateBinaryReader(fileStream, readOption);
			((ExcelBinaryReader)reader).ConvertOaDate = convertOADate;

			return reader;
		}

		/// <summary>
		/// Creates an instance of <see cref="ExcelOpenXmlReader"/>
		/// </summary>
		/// <param name="fileStream">The file stream.</param>
		/// <returns></returns>
		public IExcelDataReader CreateOpenXmlReader(Stream fileStream)
		{
			IExcelDataReader reader = new ExcelOpenXmlReader();
			reader.Initialize(fileStream);

			return reader;
		}
	}
}
