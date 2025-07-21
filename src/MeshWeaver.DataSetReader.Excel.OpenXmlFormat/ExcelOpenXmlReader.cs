#nullable enable
using System.Data;
using System.Globalization;
using System.Xml.Linq;
using MeshWeaver.DataSetReader.Excel.Utils;
using static MeshWeaver.DataSetReader.Excel.OpenXmlFormat.ExcelOpenXmlConst;

namespace MeshWeaver.DataSetReader.Excel.OpenXmlFormat
{
    public class ExcelOpenXmlReader : IExcelDataReader
    {
        #region Members

        private XlsxWorkbook _workbook = null!;
        private bool _isValid;
        private bool _isClosed;
        private bool _isFirstRead;
        private string _exceptionMessage = string.Empty;
        private int _depth;
        private int _resultIndex;
        private int _emptyRowCount;
        private ZipWorker _zipWorker = null!;
        private Stream _sheetStream = null!;
        private object[] _cellsValues = null!;
        private object[] _savedCellsValues = null!;
        private Queue<XElement> _sheetRows = null!;

        private bool _disposed;
        private bool _isFirstRowAsColumnNames;
        private const string Column = "Column";
        private readonly List<int> _defaultDateTimeStyles;

        #endregion

        internal ExcelOpenXmlReader()
        {
            _isValid = true;
            _isFirstRead = true;

            _defaultDateTimeStyles = new List<int>(new[]
            {
                14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47
            });
        }

        private void ReadGlobals()
        {
            _workbook = new XlsxWorkbook(
                _zipWorker.GetWorkbookStream()!,
                _zipWorker.GetWorkbookRelsStream()!,
                _zipWorker.GetSharedStringsStream()!,
                _zipWorker.GetStylesStream()!);

            CheckDateTimeNumFmts(_workbook.Styles.NumFmts);
        }

        private void CheckDateTimeNumFmts(List<XlsxNumFmt> list)
        {
            if (list.Count == 0) return;

            foreach (XlsxNumFmt numFmt in list)
            {
                if (string.IsNullOrEmpty(numFmt.FormatCode)) continue;
                string fc = numFmt.FormatCode.ToLower();

                int pos;
                while ((pos = fc.IndexOf('"')) > 0)
                {
                    int endPos = fc.IndexOf('"', pos + 1);

                    if (endPos > 0) fc = fc.Remove(pos, endPos - pos + 1);
                }

                //it should only detect it as a date if it contains
                //dd mm mmm yy yyyy
                //h hh ss
                //AM PM
                //and only if these appear as "words" so either contained in [ ]
                //or delimted in someway
                //updated to not detect as date if format contains a #
                FormatReader formatReader = new FormatReader { FormatString = fc };
                if (formatReader.IsDateFormatString())
                {
                    _defaultDateTimeStyles.Add(numFmt.Id);
                }
            }
        }

        private void ReadSheetGlobals(XlsxWorksheet sheet)
        {
            if (_sheetStream != null) _sheetStream.Close();

            _sheetStream = _zipWorker.GetWorksheetStream(sheet.Path)!;

            if (null == _sheetStream) return;

            XDocument doc = XDocument.Load(_sheetStream);

            var dimensionElement = doc.Descendants(SpreadSheetNamespace + DimensionTag).FirstOrDefault();
            var dimensionValue = dimensionElement?.Attribute(DimensionReferenceAttribute)?.Value;
            if (dimensionValue != null)
            {
                sheet.Dimension = new XlsxDimension(dimensionValue);
            }
            else
            {
                int cols = doc.Descendants(SpreadSheetNamespace + XlsxWorksheet.NCol).Count();
                int rows = doc.Descendants(SpreadSheetNamespace + RowTag).Count();
                if (rows == 0 || cols == 0)
                {
                    sheet.IsEmpty = true;
                }
                sheet.Dimension = new XlsxDimension(rows, cols);
            }

            //read up to the sheetData element. if this element is empty then there aren't any rows and we need to null out dimension
            var sheetData = doc.Descendants(SpreadSheetNamespace + SheetDataTag).FirstOrDefault();
            if (sheetData == null || sheetData.IsEmpty)
            {
                sheet.IsEmpty = true;
            }

            _sheetRows = new Queue<XElement>(doc.Descendants(SpreadSheetNamespace + RowTag));
        }

        private bool ReadSheetRow(XlsxWorksheet sheet)
        {
            if (_emptyRowCount != 0)
            {
                _cellsValues = new object[sheet.ColumnsCount];
                _emptyRowCount--;
                _depth++;

                return true;
            }

            if (_savedCellsValues != null)
            {
                _cellsValues = _savedCellsValues;
                _savedCellsValues = null!;
                _depth++;

                return true;
            }


            if (_sheetRows != null && _sheetRows.Count > 0)
            {
                _cellsValues = new object[sheet.ColumnsCount];

                XElement xrow = _sheetRows.Dequeue();

                var rowIndex = (int)xrow.Attribute(RowReferenceAttribute)!;
                if (rowIndex != (_depth + 1))
                    if (rowIndex != (_depth + 1))
                    {
                        _emptyRowCount = rowIndex - _depth - 1;
                    }

                foreach (var el in xrow.Descendants(SpreadSheetNamespace + CellTag))
                {
                    var xElement = el.Element(SpreadSheetNamespace + CellValueTag);
                    var current = new
                    {
                        AS = el.Attribute(CellStyleAttribute),
                        AT = el.Attribute(CellTypeAttribute),
                        AR = el.Attribute(CellReferenceAttribute),
                        Val = el.Attribute(CellTypeAttribute)?.Value == XlsxWorksheet.InlineString ? el.Value : xElement?.Value,
                    };

                    int row;
                    int col;
                    XlsxDimension.XlsxDim(current.AR!.Value, out col, out row);
                    object? val = current.Val;
                    if (current.Val != null)
                    {
                        double number;

                        CultureInfo culture = CultureInfo.InvariantCulture;

                        if (double.TryParse(current.Val, NumberStyles.Any, culture, out number))
                            val = number;

                        if (null != current.AT && current.AT.Value == XlsxWorksheet.SharedString) //if string
                        {
                            var str = val!.ToString();
                            if (!string.IsNullOrEmpty(str))
                                val = Helpers.ConvertEscapeChars(_workbook.SST[int.Parse(str)]);
                        } // Requested change 4: missing (it appears that if should be else if)
                        else if (null != current.AT && current.AT.Value == XlsxWorksheet.InlineString) //if string inline
                        {
                            val = Helpers.ConvertEscapeChars(current.Val!);
                        }
                        else if (null != current.AT && current.AT.Value == "b") //boolean
                        {
                            val = string.IsNullOrEmpty(current.Val) ? (bool?)null : current.Val == "1";
                        }
                        else if (null != current.AS) //if something else
                        {
                            XlsxXf xf = _workbook.Styles.CellXfs[(int)current.AS!];
                            if (xf.ApplyNumberFormat && val != null && val.ToString() != string.Empty && IsDateTimeStyle(xf.NumFmtId))
                                val = Helpers.ConvertFromOATime(number);
                        }
                    }

                    if (col - 1 < _cellsValues.Length)
                        _cellsValues[col - 1] = val!;
                }

                if (_emptyRowCount > 0)
                {
                    _savedCellsValues = _cellsValues!;
                    return ReadSheetRow(sheet);
                }
                _depth++;

                return true;
            }

            if (_sheetStream != null)
                _sheetStream.Close();

            return false;
        }

        private bool InitializeSheetRead()
        {
            if (ResultsCount <= 0) return false;

            ReadSheetGlobals(_workbook.Sheets[_resultIndex]);

            if (_workbook.Sheets[_resultIndex].Dimension == null) return false;

            _isFirstRead = false;

            _depth = 0;
            _emptyRowCount = 0;

            return true;
        }

        private bool IsDateTimeStyle(int styleId)
        {
            return _defaultDateTimeStyles.Contains(styleId);
        }

        #region IExcelDataReader Members

        public void Initialize(Stream fileStream)
        {
            _zipWorker = new ZipWorker();
            _zipWorker.Extract(fileStream);

            if (!_zipWorker.IsValid)
            {
                _isValid = false;
                _exceptionMessage = _zipWorker.ExceptionMessage;

                Close();

                return;
            }

            ReadGlobals();
        }

        public DataSet AsDataSet()
        {
            return AsDataSet(true);
        }

        public DataSet AsDataSet(bool convertOADateTime)
        {
            if (!_isValid) return new DataSet();

            DataSet dataset = new DataSet();

            for (int ind = 0; ind < _workbook.Sheets.Count; ind++)
            {
                DataTable table = new DataTable(_workbook.Sheets[ind].Name);

                ReadSheetGlobals(_workbook.Sheets[ind]);

                if (_workbook.Sheets[ind].Dimension == null) continue;

                _depth = 0;
                _emptyRowCount = 0;

                //DataTable columns
                if (!_isFirstRowAsColumnNames)
                {
                    for (int i = 0; i < _workbook.Sheets[ind].ColumnsCount; i++)
                    {
                        table.Columns.Add(null, typeof(Object));
                    }
                }
                else if (ReadSheetRow(_workbook.Sheets[ind]))
                {
                    for (int index = 0; index < _cellsValues.Length; index++)
                    {
                        if (_cellsValues[index] != null && _cellsValues[index].ToString()!.Length > 0)
                            Helpers.AddColumnHandleDuplicate(table, _cellsValues[index].ToString()!);
                        else
                            Helpers.AddColumnHandleDuplicate(table, string.Concat(Column, index));
                    }
                }
                else continue;

                table.BeginLoadData();

                while (ReadSheetRow(_workbook.Sheets[ind]))
                {
                    table.Rows.Add(_cellsValues);
                }

                if (table.Rows.Count > 0)
                    dataset.Tables.Add(table);
                table.EndLoadData();
            }
            dataset.AcceptChanges();
            Helpers.FixDataTypes(dataset);
            return dataset;
        }

        public bool IsFirstRowAsColumnNames
        {
            get { return _isFirstRowAsColumnNames; }
            set { _isFirstRowAsColumnNames = value; }
        }

        public bool IsValid
        {
            get { return _isValid; }
        }

        public string ExceptionMessage
        {
            get { return _exceptionMessage; }
        }

        public string Name
        {
            get { return (_resultIndex >= 0 && _resultIndex < ResultsCount) ? _workbook.Sheets[_resultIndex].Name : string.Empty; }
        }

        public void Close()
        {
            _isClosed = true;

            if (_sheetStream != null) _sheetStream.Close();
            if (_zipWorker != null) _zipWorker.Dispose();
        }

        public int Depth
        {
            get { return _depth; }
        }

        public int ResultsCount
        {
            get { return _workbook == null ? -1 : _workbook.Sheets.Count; }
        }

        public bool IsClosed
        {
            get { return _isClosed; }
        }

        public bool NextResult()
        {
            if (_resultIndex >= (ResultsCount - 1)) return false;

            _resultIndex++;

            _isFirstRead = true;

            return true;
        }

        public bool Read()
        {
            if (!_isValid) return false;

            if (_isFirstRead && !InitializeSheetRead())
            {
                return false;
            }

            return ReadSheetRow(_workbook.Sheets[_resultIndex]);
        }

        public int FieldCount
        {
            get { return _resultIndex >= 0 && _resultIndex < ResultsCount ? _workbook.Sheets[_resultIndex].ColumnsCount : -1; }
        }

        public bool GetBoolean(int i)
        {
            if (IsDBNull(i)) return false;

            return Boolean.Parse(_cellsValues[i].ToString()!);
        }

        public DateTime GetDateTime(int i)
        {
            if (IsDBNull(i)) return DateTime.MinValue;

            try
            {
                return (DateTime)_cellsValues[i];
            }
            catch (InvalidCastException)
            {
                return DateTime.MinValue;
            }
        }

        public decimal GetDecimal(int i)
        {
            if (IsDBNull(i)) return decimal.MinValue;

            return decimal.Parse(_cellsValues[i].ToString()!);
        }

        public double GetDouble(int i)
        {
            if (IsDBNull(i)) return double.MinValue;

            return double.Parse(_cellsValues[i].ToString()!);
        }

        public float GetFloat(int i)
        {
            if (IsDBNull(i)) return float.MinValue;

            return float.Parse(_cellsValues[i].ToString()!);
        }

        public short GetInt16(int i)
        {
            if (IsDBNull(i)) return short.MinValue;

            return short.Parse(_cellsValues[i].ToString()!);
        }

        public int GetInt32(int i)
        {
            if (IsDBNull(i)) return int.MinValue;

            return int.Parse(_cellsValues[i].ToString()!);
        }

        public long GetInt64(int i)
        {
            if (IsDBNull(i)) return long.MinValue;

            return long.Parse(_cellsValues[i].ToString()!);
        }

        public string GetString(int i)
        {
            if (IsDBNull(i)) return string.Empty;

            return _cellsValues[i].ToString() ?? string.Empty;
        }

        public object GetValue(int i)
        {
            return _cellsValues[i];
        }

        public bool IsDBNull(int i)
        {
            return (null == _cellsValues[i]) || (DBNull.Value == _cellsValues[i]);
        }

        public object this[int i]
        {
            get { return _cellsValues[i]; }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.

            if (!_disposed)
            {
                if (disposing)
                {
                    if (_sheetStream != null) _sheetStream.Dispose();
                    if (_zipWorker != null) _zipWorker.Dispose();
                }

                _zipWorker = null!;
                _sheetStream = null!;

                _workbook = null!;
                _cellsValues = null!;
                _savedCellsValues = null!;

                _disposed = true;
            }
        }

        ~ExcelOpenXmlReader()
        {
            Dispose(false);
        }

        #endregion

        #region  Not Supported IDataReader Members

        public DataTable GetSchemaTable()
        {
            throw new NotSupportedException();
        }

        public int RecordsAffected
        {
            get { throw new NotSupportedException(); }
        }

        #endregion

        #region Not Supported IDataRecord Members

        public byte GetByte(int i)
        {
            throw new NotSupportedException();
        }

        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
        {
            throw new NotSupportedException();
        }

        public char GetChar(int i)
        {
            throw new NotSupportedException();
        }

        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
        {
            throw new NotSupportedException();
        }

        public IDataReader GetData(int i)
        {
            throw new NotSupportedException();
        }

        public string GetDataTypeName(int i)
        {
            throw new NotSupportedException();
        }

        public Type GetFieldType(int i)
        {
            throw new NotSupportedException();
        }

        public Guid GetGuid(int i)
        {
            throw new NotSupportedException();
        }

        public string GetName(int i)
        {
            throw new NotSupportedException();
        }

        public int GetOrdinal(string name)
        {
            throw new NotSupportedException();
        }

        public int GetValues(object[] values)
        {
            throw new NotSupportedException();
        }

        public object this[string name]
        {
            get { throw new NotSupportedException(); }
        }

        #endregion
    }
}
