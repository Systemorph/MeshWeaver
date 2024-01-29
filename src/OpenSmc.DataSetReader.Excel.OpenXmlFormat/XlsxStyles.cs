// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace OpenSmc.DataSetReader.Excel.OpenXmlFormat
{
	internal class XlsxStyles
	{
		public XlsxStyles()
		{
			CellXfs = new List<XlsxXf>();
			NumFmts = new List<XlsxNumFmt>();
		}

		public List<XlsxXf> CellXfs { get; set; }

		public List<XlsxNumFmt> NumFmts { get; set; }
	}
}
