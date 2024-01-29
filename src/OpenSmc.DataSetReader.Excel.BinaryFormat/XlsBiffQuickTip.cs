// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// For now QuickTip will do nothing, it seems to have a different
	/// </summary>
	internal class XlsBiffQuickTip : XlsBiffRecord
	{

        internal XlsBiffQuickTip(byte[] bytes, uint offset, ExcelBinaryReader reader)
			: base(bytes, offset, reader)
        {
        }

	}
}