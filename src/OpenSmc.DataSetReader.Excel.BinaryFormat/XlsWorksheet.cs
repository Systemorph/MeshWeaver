// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents Worksheet section in workbook
	/// </summary>
	internal class XlsWorksheet
	{
		private readonly uint _dataOffset;
		private readonly int _index;
		private readonly string _name = string.Empty;

		public XlsWorksheet(int index, XlsBiffBoundSheet refSheet)
		{
			_index = index;
			_name = refSheet.SheetName;
			_dataOffset = refSheet.StartOffset;
		}

		/// <summary>
		/// Name of worksheet
		/// </summary>
		public string Name
		{
			get { return _name; }
		}

		/// <summary>
		/// Zero-based index of worksheet
		/// </summary>
		public int Index
		{
			get { return _index; }
		}

		/// <summary>
		/// Offset of worksheet data
		/// </summary>
		public uint DataOffset
		{
			get { return _dataOffset; }
		}

		public XlsBiffSimpleValueRecord CalcMode { get; set; }

		public XlsBiffSimpleValueRecord CalcCount { get; set; }

		public XlsBiffSimpleValueRecord RefMode { get; set; }

		public XlsBiffSimpleValueRecord Iteration { get; set; }

		public XlsBiffRecord Delta { get; set; }

		/// <summary>
		/// Dimensions of worksheet
		/// </summary>
		public XlsBiffDimensions Dimensions { get; set; }

		public XlsBiffRecord Window { get; set; }
	}
}