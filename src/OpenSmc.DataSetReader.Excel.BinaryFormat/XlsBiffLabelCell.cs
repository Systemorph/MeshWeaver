// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

using System.Text;
using OpenSmc.DataSetReader.Excel.Utils;

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents a string (max 255 bytes)
	/// </summary>
	internal class XlsBiffLabelCell : XlsBiffBlankCell
	{
		private Encoding _useEncoding = Encoding.Default;

		internal XlsBiffLabelCell(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
		{
		}

		/// <summary>
		/// Encoding used to deal with strings
		/// </summary>
		public Encoding UseEncoding
		{
			get { return _useEncoding; }
			set { _useEncoding = value; }
		}

		/// <summary>
		/// Length of string value
		/// </summary>
		public ushort Length
		{
			get { return ReadUInt16(0x6); }
		}

		/// <summary>
		/// Returns value of this cell
		/// </summary>
		public string Value
		{
			get
			{
				byte[] bts = ReadArray(reader.IsV8() ? 0x9 : 0x2, Length * (Helpers.IsSingleByteEncoding(_useEncoding) ? 1 : 2));
				
				return _useEncoding.GetString(bts, 0, bts.Length);
			}
		}
	}
}