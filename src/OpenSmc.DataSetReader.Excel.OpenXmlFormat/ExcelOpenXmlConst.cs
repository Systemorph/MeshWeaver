using System.Xml.Linq;

namespace OpenSmc.DataSetReader.Excel.OpenXmlFormat
{
    public static class ExcelOpenXmlConst
    {
        public static readonly XNamespace SpreadSheetNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public const string WorksheetTag = "worksheet";
        public const string DimensionTag = "dimension";
        public const string DimensionReferenceAttribute = "ref";
        public const string SheetDataTag = "sheetData";
        public const string RowTag = "row";
        public const string RowReferenceAttribute = "r";
        public const string CellTag = "c";
        public const string CellReferenceAttribute = "r";
        public const string CellTypeAttribute = "t";
        public const string CellStyleAttribute = "s";
        public const string CellValueTag = "v";
    }
}
