// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents a string stored in SST
	/// </summary>
	internal class XlsBiffLabelSSTCell : XlsBiffBlankCell
	{
		internal XlsBiffLabelSSTCell(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
		{
		}

		/// <summary>
		/// Index of string in Shared String Table
		/// </summary>
		public uint SSTIndex
		{
			get { return base.ReadUInt32(0x6); }
		}

		/// <summary>
		/// Returns text using specified SST
		/// </summary>
		/// <param name="sst">Shared String Table record</param>
		/// <returns></returns>
		public string Text(XlsBiffSST sst)
		{
			return sst.GetString(SSTIndex);
		}
	}
}