using System.Xml.Linq;

namespace MeshWeaver.DataSetReader.Excel.OpenXmlFormat
{
    /// <summary>
    /// XML element/attribute names and the namespace used by the SpreadsheetML (OpenXML, <c>.xlsx</c>) worksheet format.
    /// </summary>
    public static class ExcelOpenXmlConst
    {
        /// <summary>The SpreadsheetML XML namespace shared by all worksheet, workbook and shared-string elements.</summary>
        public static readonly XNamespace SpreadSheetNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>Root element name of a worksheet part.</summary>
        public const string WorksheetTag = "worksheet";
        /// <summary>Element name describing the used cell range (dimension) of a worksheet.</summary>
        public const string DimensionTag = "dimension";
        /// <summary>Attribute name (on the dimension element) holding the used-range reference, e.g. <c>A1:C10</c>.</summary>
        public const string DimensionReferenceAttribute = "ref";
        /// <summary>Element name wrapping the rows of a worksheet.</summary>
        public const string SheetDataTag = "sheetData";
        /// <summary>Element name of a single worksheet row.</summary>
        public const string RowTag = "row";
        /// <summary>Attribute name (on a row element) holding the 1-based row index.</summary>
        public const string RowReferenceAttribute = "r";
        /// <summary>Element name of a single cell.</summary>
        public const string CellTag = "c";
        /// <summary>Attribute name (on a cell element) holding its A1-style reference, e.g. <c>B3</c>.</summary>
        public const string CellReferenceAttribute = "r";
        /// <summary>Attribute name (on a cell element) holding its value type, e.g. shared string or boolean.</summary>
        public const string CellTypeAttribute = "t";
        /// <summary>Attribute name (on a cell element) holding the index of its cell-format/style.</summary>
        public const string CellStyleAttribute = "s";
        /// <summary>Element name holding a cell's raw value.</summary>
        public const string CellValueTag = "v";
    }
}
