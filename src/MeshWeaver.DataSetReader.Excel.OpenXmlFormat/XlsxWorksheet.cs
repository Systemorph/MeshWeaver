// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/
namespace MeshWeaver.DataSetReader.Excel.OpenXmlFormat
{
	internal class XlsxWorksheet
	{
		public const string NCol = "col";
		public const string SharedString = "s";
		public const string InlineString = "inlineStr";

		public bool IsEmpty { get; set; }
		public XlsxDimension Dimension { get; set; }

		public int ColumnsCount
		{
			get
			{
				return IsEmpty ? 0 : (Dimension == null ? -1 : Dimension.LastCol);
			}
		}

		public int RowsCount
		{
			get
			{
				return Dimension == null ? -1 : Dimension.LastRow - Dimension.FirstRow + 1;
			}
		}

		private readonly string _name;

		public string Name
		{
			get { return _name; }
		}

		private readonly int _id;

		public int Id
		{
			get { return _id; }
		}

		public string RelationId { get; set; }
		public string Path { get; set; }

		public XlsxWorksheet(string name, int id, string relationId)
		{
			_name = name;
			_id = id;
			RelationId = relationId;
		}

	}
}
