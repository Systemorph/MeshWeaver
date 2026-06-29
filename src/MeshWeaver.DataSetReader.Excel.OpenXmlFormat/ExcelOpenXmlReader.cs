#nullable enable
using System.Data;
using System.Globalization;
using System.Xml.Linq;
using MeshWeaver.DataSetReader.Excel.Utils;
using static MeshWeaver.DataSetReader.Excel.OpenXmlFormat.ExcelOpenXmlConst;

namespace MeshWeaver.DataSetReader.Excel.OpenXmlFormat
{
    /// <summary>
    /// An <see cref="IExcelDataReader"/> implementation that streams rows from an OpenXML (<c>.xlsx</c>) workbook,
    /// exposing each worksheet as a forward-only data reader and materialising the whole workbook as a <see cref="DataSet"/>.
    /// </summary>
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

        /// <summary>Opens the workbook from the supplied stream, extracts its parts and reads the global workbook metadata.</summary>
        /// <param name="fileStream">The <c>.xlsx</c> package stream to read.</param>
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

        /// <summary>Reads every worksheet into a <see cref="DataSet"/>, converting OLE-automation date serials to <see cref="DateTime"/>.</summary>
        /// <returns>A <see cref="DataSet"/> with one <see cref="DataTable"/> per non-empty worksheet.</returns>
        public DataSet AsDataSet()
        {
            return AsDataSet(true);
        }

        /// <summary>Reads every worksheet into a <see cref="DataSet"/>.</summary>
        /// <param name="convertOADateTime">When <c>true</c>, OLE-automation date serial numbers in date-formatted cells are converted to <see cref="DateTime"/>.</param>
        /// <returns>A <see cref="DataSet"/> with one <see cref="DataTable"/> per non-empty worksheet.</returns>
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

        /// <summary>Gets or sets whether the first row of each sheet supplies the column names instead of data.</summary>
        public bool IsFirstRowAsColumnNames
        {
            get { return _isFirstRowAsColumnNames; }
            set { _isFirstRowAsColumnNames = value; }
        }

        /// <summary>Gets whether the workbook was opened successfully and is readable.</summary>
        public bool IsValid
        {
            get { return _isValid; }
        }

        /// <summary>Gets the message describing why the workbook is invalid, or an empty string when valid.</summary>
        public string ExceptionMessage
        {
            get { return _exceptionMessage; }
        }

        /// <summary>Gets the name of the worksheet at the current result index, or an empty string when out of range.</summary>
        public string Name
        {
            get { return (_resultIndex >= 0 && _resultIndex < ResultsCount) ? _workbook.Sheets[_resultIndex].Name : string.Empty; }
        }

        /// <summary>Closes the reader, releasing the current sheet stream and the underlying zip package.</summary>
        public void Close()
        {
            _isClosed = true;

            if (_sheetStream != null) _sheetStream.Close();
            if (_zipWorker != null) _zipWorker.Dispose();
        }

        /// <summary>Gets the zero-based index of the current row within the active worksheet.</summary>
        public int Depth
        {
            get { return _depth; }
        }

        /// <summary>Gets the number of worksheets in the workbook, or <c>-1</c> if no workbook has been loaded.</summary>
        public int ResultsCount
        {
            get { return _workbook == null ? -1 : _workbook.Sheets.Count; }
        }

        /// <summary>Gets whether the reader has been closed.</summary>
        public bool IsClosed
        {
            get { return _isClosed; }
        }

        /// <summary>Advances the reader to the next worksheet.</summary>
        /// <returns><c>true</c> if another worksheet is available; otherwise <c>false</c>.</returns>
        public bool NextResult()
        {
            if (_resultIndex >= (ResultsCount - 1)) return false;

            _resultIndex++;

            _isFirstRead = true;

            return true;
        }

        /// <summary>Advances to the next row of the current worksheet.</summary>
        /// <returns><c>true</c> if a row was read; otherwise <c>false</c>.</returns>
        public bool Read()
        {
            if (!_isValid) return false;

            if (_isFirstRead && !InitializeSheetRead())
            {
                return false;
            }

            return ReadSheetRow(_workbook.Sheets[_resultIndex]);
        }

        /// <summary>Gets the number of columns in the current worksheet, or <c>-1</c> when no worksheet is active.</summary>
        public int FieldCount
        {
            get { return _resultIndex >= 0 && _resultIndex < ResultsCount ? _workbook.Sheets[_resultIndex].ColumnsCount : -1; }
        }

        /// <summary>Gets the value of the specified column as a <see cref="bool"/>.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The boolean value, or <c>false</c> if the cell is null.</returns>
        public bool GetBoolean(int i)
        {
            if (IsDBNull(i)) return false;

            return Boolean.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a <see cref="DateTime"/>.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The date/time value, or <see cref="DateTime.MinValue"/> if the cell is null or not a date.</returns>
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

        /// <summary>Gets the value of the specified column as a <see cref="decimal"/>.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The decimal value, or <see cref="decimal.MinValue"/> if the cell is null.</returns>
        public decimal GetDecimal(int i)
        {
            if (IsDBNull(i)) return decimal.MinValue;

            return decimal.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a <see cref="double"/>.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The double value, or <see cref="double.MinValue"/> if the cell is null.</returns>
        public double GetDouble(int i)
        {
            if (IsDBNull(i)) return double.MinValue;

            return double.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a <see cref="float"/>.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The float value, or <see cref="float.MinValue"/> if the cell is null.</returns>
        public float GetFloat(int i)
        {
            if (IsDBNull(i)) return float.MinValue;

            return float.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a 16-bit integer.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The <see cref="short"/> value, or <see cref="short.MinValue"/> if the cell is null.</returns>
        public short GetInt16(int i)
        {
            if (IsDBNull(i)) return short.MinValue;

            return short.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a 32-bit integer.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The <see cref="int"/> value, or <see cref="int.MinValue"/> if the cell is null.</returns>
        public int GetInt32(int i)
        {
            if (IsDBNull(i)) return int.MinValue;

            return int.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a 64-bit integer.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The <see cref="long"/> value, or <see cref="long.MinValue"/> if the cell is null.</returns>
        public long GetInt64(int i)
        {
            if (IsDBNull(i)) return long.MinValue;

            return long.Parse(_cellsValues[i].ToString()!);
        }

        /// <summary>Gets the value of the specified column as a string.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The string value, or an empty string if the cell is null.</returns>
        public string GetString(int i)
        {
            if (IsDBNull(i)) return string.Empty;

            return _cellsValues[i].ToString() ?? string.Empty;
        }

        /// <summary>Gets the raw value of the specified column.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>The cell value as stored.</returns>
        public object GetValue(int i)
        {
            return _cellsValues[i];
        }

        /// <summary>Determines whether the specified column holds a null/empty value.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns><c>true</c> if the cell is null or <see cref="DBNull"/>; otherwise <c>false</c>.</returns>
        public bool IsDBNull(int i)
        {
            return (null == _cellsValues[i]) || (DBNull.Value == _cellsValues[i]);
        }

        /// <summary>Gets the raw value of the column at the specified ordinal in the current row.</summary>
        /// <param name="i">Zero-based column index.</param>
        public object this[int i]
        {
            get { return _cellsValues[i]; }
        }

        #endregion

        #region IDisposable Members

        /// <summary>Releases all resources held by the reader, including the sheet stream and zip package.</summary>
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

        /// <summary>Finalizer that releases unmanaged resources if <see cref="Dispose()"/> was not called.</summary>
        ~ExcelOpenXmlReader()
        {
            Dispose(false);
        }

        #endregion

        #region  Not Supported IDataReader Members

        /// <summary>Not supported by this reader.</summary>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public DataTable GetSchemaTable()
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader; always throws <see cref="NotSupportedException"/>.</summary>
        public int RecordsAffected
        {
            get { throw new NotSupportedException(); }
        }

        #endregion

        #region Not Supported IDataRecord Members

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public byte GetByte(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <param name="fieldOffset">Offset within the field at which to start reading.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <param name="bufferoffset">Offset within <paramref name="buffer"/> at which to start writing.</param>
        /// <param name="length">Maximum number of bytes to read.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public char GetChar(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <param name="fieldoffset">Offset within the field at which to start reading.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <param name="bufferoffset">Offset within <paramref name="buffer"/> at which to start writing.</param>
        /// <param name="length">Maximum number of characters to read.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public IDataReader GetData(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public string GetDataTypeName(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public Type GetFieldType(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public Guid GetGuid(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="i">Zero-based column index.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public string GetName(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="name">Column name to look up.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public int GetOrdinal(string name)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader.</summary>
        /// <param name="values">Array that would receive the column values.</param>
        /// <returns>This method never returns; it always throws.</returns>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public int GetValues(object[] values)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported by this reader; always throws <see cref="NotSupportedException"/>.</summary>
        /// <param name="name">Column name.</param>
        public object this[string name]
        {
            get { throw new NotSupportedException(); }
        }

        #endregion
    }
}
