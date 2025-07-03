// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

using System.Xml.Linq;
using static MeshWeaver.DataSetReader.Excel.OpenXmlFormat.ExcelOpenXmlConst;

namespace MeshWeaver.DataSetReader.Excel.OpenXmlFormat
{
	internal class XlsxWorkbook
	{
		private const string NSheet = "sheet";
		private const string NT = "t";
		private const string NSi = "si";
		private const string NCellXfs = "cellXfs";
		private const string NNumFmts = "numFmts";

		private const string ASheetId = "sheetId";
		private const string AName = "name";
		private const string ARid = "id";

		private const string NRel = "Relationship";
		private const string AId = "Id";
		private const string ATarget = "Target";


		public XlsxWorkbook(Stream workbookStream, Stream relsStream, Stream sharedStringsStream, Stream stylesStream)
		{
			if (null == workbookStream) throw new ArgumentNullException();

			ReadWorkbook(workbookStream);

			ReadWorkbookRels(relsStream);

			ReadSharedStrings(sharedStringsStream);

			ReadStyles(stylesStream);
		}

		private List<XlsxWorksheet> _sheets = null!;

		public List<XlsxWorksheet> Sheets
		{
			get { return _sheets; }
			set { _sheets = value; }
		}

		private List<string> _SST = null!;

		public List<string> SST
		{
			get { return _SST; }
		}

		private XlsxStyles _styles = null!;

		public XlsxStyles Styles
		{
			get { return _styles; }
		}


		private void ReadStyles(Stream xmlFileStream)
		{
			if (null == xmlFileStream) return;

			XDocument doc = XDocument.Load(xmlFileStream);
			_styles = new XlsxStyles
			{
				NumFmts = doc.Descendants(SpreadSheetNamespace + XlsxNumFmt.N_numFmt)
					.Select(
						el => new XlsxNumFmt((int) el.Attribute(XlsxNumFmt.A_numFmtId)!, el.Attribute(XlsxNumFmt.A_formatCode)!.Value))
					.ToList(),
				CellXfs = doc.Descendants(SpreadSheetNamespace + XlsxXf.CellStyles).Descendants(SpreadSheetNamespace + XlsxXf.N_xf)
					.Select(
						el =>
							new
							{
								Id = el.Attribute(XlsxXf.A_xfId),
								NumFormatId = el.Attribute(XlsxXf.A_numFmtId),
								Value = el.Attribute(XlsxXf.A_applyNumberFormat)
							})
					.Select(
						val =>
							new XlsxXf(val.Id == null ? -1 : (int)val.Id!,
								val.NumFormatId == null ? -1 : (int)val.NumFormatId!, val.Value?.Value ?? string.Empty)).ToList()
			};


			xmlFileStream.Close();
			
		}

		private void ReadSharedStrings(Stream xmlFileStream)
		{
			if (null == xmlFileStream) return;
			
			XDocument doc = XDocument.Load(xmlFileStream);
			_SST = doc.Descendants(SpreadSheetNamespace + NSi).Select(el => string.Join(string.Empty, el.Descendants(SpreadSheetNamespace + NT).Select(t => t.Value))).ToList();

			xmlFileStream.Close();
			
		}


		private void ReadWorkbook(Stream xmlFileStream)
		{
			XDocument doc = XDocument.Load(xmlFileStream);

			_sheets =
				doc.Descendants(SpreadSheetNamespace + NSheet)
					.Select(
						el =>
							new XlsxWorksheet(el.Attribute(AName)!.Value, (int) el.Attribute(ASheetId)!,
								el.Attribute(Namespaces.Relation + ARid)!.Value))
					.ToList();
			
			xmlFileStream.Close();
		}

		private void ReadWorkbookRels(Stream xmlFileStream)
		{
			XDocument doc = XDocument.Load(xmlFileStream);
			foreach (
				var param in
					doc.Descendants(Namespaces.PackageRelation + NRel)
						.Select(el => new {AId = el.Attribute(AId)!.Value, ATarget = el.Attribute(ATarget)!.Value}))
			{
				XlsxWorksheet? tempSheet = _sheets.FirstOrDefault(sh => sh.RelationId == param.AId);

				if (tempSheet != null)
				{
					tempSheet.Path = param.ATarget;
				}
			}


			xmlFileStream.Close();
		}
		
	}
}
