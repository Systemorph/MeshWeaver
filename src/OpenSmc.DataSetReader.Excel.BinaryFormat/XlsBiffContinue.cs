// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents additional space for very large records
	/// </summary>
	internal class XlsBiffContinue : XlsBiffRecord
	{
		internal XlsBiffContinue(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
		{
		}

	}
}
