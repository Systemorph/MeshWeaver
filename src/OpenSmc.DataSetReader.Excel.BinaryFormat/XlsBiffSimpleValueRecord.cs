// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/
namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents record with the only two-bytes value
	/// </summary>
	internal class XlsBiffSimpleValueRecord : XlsBiffRecord
	{
		internal XlsBiffSimpleValueRecord(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
		{
		}

		/// <summary>
		/// Returns value
		/// </summary>
		public ushort Value
		{
			get { return ReadUInt16(0x0); }
		}
	}
}
