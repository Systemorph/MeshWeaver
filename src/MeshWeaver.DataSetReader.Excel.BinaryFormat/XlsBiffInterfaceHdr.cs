// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{

	/// <summary>
	/// Represents InterfaceHdr record in Wokrbook Globals
	/// </summary>
	internal class XlsBiffInterfaceHdr : XlsBiffRecord
	{
		internal XlsBiffInterfaceHdr(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
		{
		}

		/// <summary>
		/// Returns CodePage for Interface Header
		/// </summary>
		public ushort CodePage
		{
			get { return base.ReadUInt16(0x0); }
		}
	}
}
