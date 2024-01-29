// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents Dimensions of worksheet
	/// </summary>
	internal class XlsBiffDimensions : XlsBiffRecord
	{
		internal XlsBiffDimensions(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
		{
			IsV8 = true;
		}

		/// <summary>
		/// Gets or sets if BIFF8 addressing is used
		/// </summary>
		public bool IsV8 { get; set; }

		/// <summary>
		/// Index of first row
		/// </summary>
		public uint FirstRowIndex
		{
			get { return IsV8 ? ReadUInt32(0x0) : ReadUInt16(0x0); }
		}

		/// <summary>
		/// Index of last row
		/// </summary>
		public uint LastRowIndex
		{
			get { return IsV8 ? ReadUInt32(0x4) : (uint) (ReadUInt16(0x2) - 1); }
		}

		/// <summary>
		/// Index of first column
		/// </summary>
		public ushort FirstColumnIndex
		{
			get { return IsV8 ? ReadUInt16(0x8) : ReadUInt16(0x4); }
		}

		/// <summary>
		/// Index of last column 
		/// </summary>
		public ushort LastColumnIndex
		{
			get { return IsV8 ? (ushort)(ReadUInt16(0x9) >> 8) : (ushort)(ReadUInt16(0x6) - 1); }
		}
	}
}