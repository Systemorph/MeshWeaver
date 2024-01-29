// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/
namespace OpenSmc.DataSetReader.Excel.OpenXmlFormat
{
	internal class XlsxNumFmt
	{
		public const string N_numFmt = "numFmt";
		public const string A_numFmtId = "numFmtId";
		public const string A_formatCode = "formatCode";

		public int Id { get; set; }

		public string FormatCode { get; set; }

		public XlsxNumFmt(int id, string formatCode)
		{
			Id = id;
			FormatCode = formatCode;
		}
	}
}
