using System.Data;
using System.Text;
using MeshWeaver.DataSetReader.Excel.Utils;

namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// ExcelDataReader Class
	/// </summary>
	public class ExcelBinaryReader : IExcelDataReader
	{
		#region Members

		private Stream _file = null!;
		private XlsHeader _hdr = null!;
		private List<XlsWorksheet> _sheets = null!;
		private XlsBiffStream _stream = null!;
		private DataSet _workbookData = null!;
		private XlsWorkbookGlobals _globals = null!;
		private ushort _version;
		private Encoding _encoding;
		private bool _isValid;
		private bool _isClosed;
		private readonly Encoding _defaultEncoding = Encoding.UTF8;
		private string _exceptionMessage = null!;
		private object[] _cellsValues = null!;
		private uint[] _dbCellAddrs = null!;
		private int _dbCellAddrsIndex;
		private bool _canRead;
		private int _sheetIndex;
		private int _rowIndex;
		private int _cellOffset;
		private int _maxCol;
		private int _lastRowIndex;
		private bool _noIndex;
		private XlsBiffRow _currentRowRecord = null!;
		private readonly ReadOption _readOption = ReadOption.Strict;

		private bool _isFirstRead;
		private bool _isFirstRowAsColumnNames;

		private const string Workbook = "Workbook";
		private const string Book = "Book";
		private const string Column = "Column";

		private bool _disposed;

		#endregion

		internal ExcelBinaryReader()
		{
			_encoding = _defaultEncoding;
			_version = 0x0600;
			_isValid = true;

			_isFirstRead = true;
		}

		internal ExcelBinaryReader(ReadOption readOption) : this()
		{
			_readOption = readOption;
		}

		#region IDisposable Members

		/// <summary>Releases all resources held by the reader, including the workbook data and worksheet list.</summary>
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
					if (_workbookData != null) _workbookData.Dispose();

					if (_sheets != null) _sheets.Clear();
				}

				_workbookData = null!;
				_sheets = null!;
				_stream = null!;
				_globals = null!;
				_encoding = null!;
				_hdr = null!;

				_disposed = true;
			}
		}

		/// <summary>Finalizer that releases resources if <see cref="Dispose()"/> was not called.</summary>
		~ExcelBinaryReader()
		{
			Dispose(false);
		}

		#endregion

		#region Private methods

		private int FindFirstDataCellOffset(int startOffset)
		{
			//seek to the first dbcell record
			XlsBiffRecord? record = _stream.ReadAt(startOffset);
			while (!(record is XlsBiffDbCell))
			{
				if (_stream.Position >= _stream.Size)
					return -1;

				if (record is XlsBiffEOF)
					return -1;

				record = _stream.Read()!;
			}

			XlsBiffDbCell startCell = (XlsBiffDbCell)record;

			int offs = startCell.RowAddress;

			do
			{
				XlsBiffRow? row = _stream.ReadAt(offs) as XlsBiffRow;
				if (row == null) break;

				offs += row.Size;
			} while (true);

			return offs;
		}

		private void ReadWorkBookGlobals()
		{

			ReadHeader();
			_globals = new XlsWorkbookGlobals();
			_stream.Seek(0, SeekOrigin.Begin);

			XlsBiffRecord rec = _stream.Read()!;
			XlsBiffBOF? bof = rec as XlsBiffBOF;

			if (bof == null || bof.Type != BIFFTYPE.WorkbookGlobals)
			{
				throw Fail(Errors.ErrorWorkbookGlobalsInvalidData);
			}

			bool sst = false;

			_version = bof.Version;
			_sheets = new List<XlsWorksheet>();

			while (null != (rec = _stream.Read()!))
			{
				switch (rec.ID)
				{
					case BIFFRECORDTYPE.INTERFACEHDR:
						_globals.InterfaceHdr = (XlsBiffInterfaceHdr)rec;
						break;
					case BIFFRECORDTYPE.BOUNDSHEET:
						XlsBiffBoundSheet sheet = (XlsBiffBoundSheet)rec;

						if (sheet.Type != XlsBiffBoundSheet.SheetType.Worksheet) break;

						sheet.IsV8 = IsV8();
						sheet.UseEncoding = _encoding;
						//LogManager.Log(this).Debug("BOUNDSHEET IsV8={0}", sheet.IsV8);

						_sheets.Add(new XlsWorksheet(_globals.Sheets.Count, sheet));
						_globals.Sheets.Add(sheet);

						break;
					case BIFFRECORDTYPE.MMS:
						_globals.MMS = rec;
						break;
					case BIFFRECORDTYPE.COUNTRY:
						_globals.Country = rec;
						break;
					case BIFFRECORDTYPE.CODEPAGE:

						_globals.CodePage = (XlsBiffSimpleValueRecord)rec;

						try
						{
							_encoding = Encoding.GetEncoding(_globals.CodePage.Value);
						}
						catch (ArgumentException)
						{
							// Warning - Password protection
							// TODO: Attach to ILog
						}

						break;
					case BIFFRECORDTYPE.FONT:
					case BIFFRECORDTYPE.FONT_V34:
						_globals.Fonts.Add(rec);
						break;
					case BIFFRECORDTYPE.FORMAT_V23:
						{
							XlsBiffFormatString fmt = (XlsBiffFormatString)rec;
							fmt.UseEncoding = _encoding;
							_globals.Formats.Add((ushort)_globals.Formats.Count, fmt);
						}
						break;
					case BIFFRECORDTYPE.FORMAT:
						{
							XlsBiffFormatString fmt = (XlsBiffFormatString)rec;
							_globals.Formats.Add(fmt.Index, fmt);
						}
						break;
					case BIFFRECORDTYPE.XF:
					case BIFFRECORDTYPE.XF_V4:
					case BIFFRECORDTYPE.XF_V3:
					case BIFFRECORDTYPE.XF_V2:
						_globals.ExtendedFormats.Add(rec);
						break;
					case BIFFRECORDTYPE.SST:
						_globals.SST = (XlsBiffSST)rec;
						sst = true;
						break;
					case BIFFRECORDTYPE.CONTINUE:
						if (!sst) break;
						_globals.SST?.Append((XlsBiffContinue)rec);
						break;
					case BIFFRECORDTYPE.EXTSST:
						_globals.ExtSST = rec;
						sst = false;
						break;
					case BIFFRECORDTYPE.PROTECT:
					case BIFFRECORDTYPE.PASSWORD:
					case BIFFRECORDTYPE.PROT4REVPASSWORD:
						//IsProtected
						break;
					case BIFFRECORDTYPE.EOF:
						if (_globals.SST != null)
							_globals.SST.ReadStrings();
						return;

					default:
						continue;
				}
			}
		}

		//Read Header
		private void ReadHeader()
		{
			try
			{
				_hdr = XlsHeader.ReadHeader(_file);
			}
			catch (HeaderException ex)
			{
				throw Fail(ex.Message, ex);
			}
			catch (FormatException ex)
			{
				throw Fail(ex.Message, ex);
			}

			XlsRootDirectory dir = new XlsRootDirectory(_hdr);
			XlsDirectoryEntry? workbookEntry = dir.FindEntry(Workbook) ?? dir.FindEntry(Book);

			if (workbookEntry == null)
			{
				throw Fail(Errors.ErrorStreamWorkbookNotFound);
			}

			if (workbookEntry.EntryType != STGTY.STGTY_STREAM)
			{
				throw Fail(Errors.ErrorWorkbookIsNotStream);
			}

			_stream = new XlsBiffStream(_hdr, workbookEntry.StreamFirstSector, workbookEntry.IsEntryMiniStream, dir, this);
		}

		private bool ReadWorkSheetGlobals(XlsWorksheet sheet, out XlsBiffIndex idx, out XlsBiffRow row)
		{
			idx = null!;
			row = null!;

			_stream.Seek((int)sheet.DataOffset, SeekOrigin.Begin);

			XlsBiffBOF? bof = _stream.Read() as XlsBiffBOF;
			if (bof == null || bof.Type != BIFFTYPE.Worksheet) return false;

			XlsBiffRecord? rec = _stream.Read();
			if (rec == null) return false;
			if (rec is XlsBiffIndex)
			{
				idx = (XlsBiffIndex)rec;
			}
			else if (rec is XlsBiffUncalced)
			{
				// Sometimes this come before the index...
				idx = _stream.Read() as XlsBiffIndex ?? null!;
			}

			//if (null == idx)
			//{
			//	// There is a record before the index! Chech his type and see the MS Biff Documentation
			//	return false;
			//}

			if (idx != null)
			{
				idx.IsV8 = IsV8();
				//LogManager.Log(this).Debug("INDEX IsV8={0}", idx.IsV8);
			}


			XlsBiffRecord trec;
			XlsBiffDimensions? dims = null;
			XlsBiffRow? rowRecord = null;

			do
			{
				trec = _stream.Read()!;
				if (trec?.ID == BIFFRECORDTYPE.DIMENSIONS)
				{
					dims = (XlsBiffDimensions)trec;
					dims.IsV8 = IsV8();
					break;
				}
			} while (trec?.ID != BIFFRECORDTYPE.ROW);


			//if we are already on row record then set that as the row, otherwise step forward till we get to a row record
			if (trec?.ID == BIFFRECORDTYPE.ROW)
				rowRecord = (XlsBiffRow)trec;

			while (rowRecord == null)
			{
				if (_stream.Position >= _stream.Size)
					break;
				XlsBiffRecord? thisRec = _stream.Read();

				//LogManager.Log(this).Debug("finding rowRecord offset {0}, rec: {1}", thisRec.Offset, thisRec.ID);
				if (thisRec is XlsBiffEOF)
					break;
				rowRecord = thisRec as XlsBiffRow;
			}

			//if (rowRecord != null)
			//	LogManager.Log(this)
			//		.Debug("Got row {0}, rec: id={1},rowindex={2}, rowColumnStart={3}, rowColumnEnd={4}", rowRecord.Offset,
			//			rowRecord.ID, rowRecord.RowIndex, rowRecord.FirstDefinedColumn, rowRecord.LastDefinedColumn);

			row = rowRecord!;

			if (dims != null)
			{
				//LogManager.Log(this).Debug("dims IsV8={0}", dims.IsV8);
				_maxCol = dims.LastColumnIndex;

				//handle case where sheet reports last column is 1 but there are actually more
				if (_maxCol <= 0 && rowRecord != null)
				{
					_maxCol = rowRecord.LastDefinedColumn;
				}

				_lastRowIndex = (int)dims.LastRowIndex;
				sheet.Dimensions = dims;
			}
			else if (idx != null)
			{
				_maxCol = 256;
				_lastRowIndex = (int)idx.LastExistingRowIndex;
			}

			if (idx != null && idx.LastExistingRowIndex < idx.FirstExistingRowIndex)
			{
				return false;
			}
			if (row == null)
			{
				return false;
			}

			_rowIndex = 0;

			return true;
		}

		private bool ReadWorkSheetRow()
		{
			_cellsValues = new object[_maxCol];

			while (_cellOffset < _stream.Size)
			{
				XlsBiffRecord? rec = _stream.ReadAt(_cellOffset);
				if (rec == null) break;
				_cellOffset += rec.Size;

				if ((rec is XlsBiffDbCell))
				{
					break;
				} //break;
				if (rec is XlsBiffEOF)
				{
					return false;
				}

				XlsBiffBlankCell? cell = rec as XlsBiffBlankCell;

				if ((null == cell) || (cell.ColumnIndex >= _maxCol)) continue;
				if (cell.RowIndex != _rowIndex)
				{
					_cellOffset -= rec.Size;
					break;
				}


				PushCellValue(cell);
			}

			_rowIndex++;

			return _rowIndex <= _lastRowIndex;
		}

		private DataTable? ReadWholeWorkSheet(XlsWorksheet sheet)
		{
			XlsBiffIndex idx;

			if (!ReadWorkSheetGlobals(sheet, out idx, out _currentRowRecord)) return null;

			DataTable table = new DataTable(sheet.Name);
			if (idx != null)
				ReadWholeWorkSheetWithIndex(idx, table);
			else
				ReadWholeWorkSheetNoIndex(table);

			table.EndLoadData();
			return table;
		}

		//TODO: quite a bit of duplication with the noindex version
		private void ReadWholeWorkSheetWithIndex(XlsBiffIndex idx, DataTable table)
		{
			bool triggerCreateColumns = true;
			_dbCellAddrs = idx.DbCellAddresses;

			for (int index = 0; index < _dbCellAddrs.Length; index++)
			{
				// init reading data
				_cellOffset = FindFirstDataCellOffset((int)_dbCellAddrs[index]);
				if (_cellOffset < 0)
					return;

				//DataTable columns
				if (triggerCreateColumns)
				{
					if (_isFirstRowAsColumnNames && ReadWorkSheetRow() || (_isFirstRowAsColumnNames && _lastRowIndex == 1))
					{
						for (int i = 0; i < _maxCol; i++)
						{
							if (_cellsValues[i] != null && _cellsValues[i].ToString()!.Length > 0)
								Helpers.AddColumnHandleDuplicate(table, _cellsValues[i].ToString()!);
							else
								Helpers.AddColumnHandleDuplicate(table, string.Concat(Column, i));
						}
					}
					else
					{
						for (int i = 0; i < _maxCol; i++)
						{
							table.Columns.Add(null, typeof(Object));
						}
					}

					triggerCreateColumns = false;

					table.BeginLoadData();
				}

				while (ReadWorkSheetRow())
				{
					table.Rows.Add(_cellsValues);
				}
			}
		}

		private void ReadWholeWorkSheetNoIndex(DataTable table)
		{
			bool triggerCreateColumns = true;
			while (Read())
			{
				//DataTable columns
				if (triggerCreateColumns)
				{
					if (_isFirstRowAsColumnNames)
					{
						for (int i = 0; i < _maxCol; i++)
						{
							if (_cellsValues[i] != null && _cellsValues[i].ToString()!.Length > 0)
								Helpers.AddColumnHandleDuplicate(table, _cellsValues[i].ToString()!);
							else
								Helpers.AddColumnHandleDuplicate(table, string.Concat(Column, i));
						}
					}
					else
					{
						for (int i = 0; i < _maxCol; i++)
						{
							table.Columns.Add(null, typeof(object));
						}
					}

					triggerCreateColumns = false;
					table.BeginLoadData();

				}
				else
				{

					table.Rows.Add(_cellsValues);
				}
			}

		}

		private void PushCellValue(XlsBiffBlankCell cell)
		{
			//LogManager.Log(this).Debug("pushCellValue {0}", cell.ID);
			switch (cell.ID)
			{
				case BIFFRECORDTYPE.BOOLERR:
					if (cell.ReadByte(7) == 0)
						_cellsValues[cell.ColumnIndex] = cell.ReadByte(6) != 0;
					break;
				case BIFFRECORDTYPE.BOOLERR_OLD:
					if (cell.ReadByte(8) == 0)
						_cellsValues[cell.ColumnIndex] = cell.ReadByte(7) != 0;
					break;
				case BIFFRECORDTYPE.INTEGER:
				case BIFFRECORDTYPE.INTEGER_OLD:
					_cellsValues[cell.ColumnIndex] = ((XlsBiffIntegerCell)cell).Value;
					break;
				case BIFFRECORDTYPE.NUMBER:
				case BIFFRECORDTYPE.NUMBER_OLD:

					double dValue = ((XlsBiffNumberCell)cell).Value;

					_cellsValues[cell.ColumnIndex] = !ConvertOaDate
						? dValue
						: TryConvertOADateTime(dValue, cell.XFormat);

					//LogManager.Log(this).Debug("VALUE: {0}", dValue);
					break;
				case BIFFRECORDTYPE.LABEL:
				case BIFFRECORDTYPE.LABEL_OLD:
				case BIFFRECORDTYPE.RSTRING:

					_cellsValues[cell.ColumnIndex] = ((XlsBiffLabelCell)cell).Value;

					//LogManager.Log(this).Debug("VALUE: {0}", _cellsValues[cell.ColumnIndex]);
					break;
				case BIFFRECORDTYPE.LABELSST:
					string tmp = _globals.SST?.GetString(((XlsBiffLabelSSTCell)cell).SSTIndex) ?? string.Empty;
					//LogManager.Log(this).Debug("VALUE: {0}", tmp);
					_cellsValues[cell.ColumnIndex] = tmp;
					break;
				case BIFFRECORDTYPE.RK:

					dValue = ((XlsBiffRKCell)cell).Value;

					_cellsValues[cell.ColumnIndex] = !ConvertOaDate
						? dValue
						: TryConvertOADateTime(dValue, cell.XFormat);

					//LogManager.Log(this).Debug("VALUE: {0}", dValue);
					break;
				case BIFFRECORDTYPE.MULRK:

					XlsBiffMulRKCell rkCell = (XlsBiffMulRKCell)cell;
					for (ushort j = cell.ColumnIndex; j <= rkCell.LastColumnIndex; j++)
					{
						dValue = rkCell.GetValue(j);
						//LogManager.Log(this).Debug("VALUE[{1}]: {0}", dValue, j);
						_cellsValues[j] = !ConvertOaDate ? dValue : TryConvertOADateTime(dValue, rkCell.GetXF(j));
					}

					break;
				case BIFFRECORDTYPE.BLANK:
				case BIFFRECORDTYPE.BLANK_OLD:
				case BIFFRECORDTYPE.MULBLANK:
					// Skip blank cells

					break;
				case BIFFRECORDTYPE.FORMULA:
				case BIFFRECORDTYPE.FORMULA_OLD:

					object? oValue = ((XlsBiffFormulaCell)cell).Value;

					if (!(oValue is FORMULAERROR))
					{
						_cellsValues[cell.ColumnIndex] = !ConvertOaDate
							? oValue!
							: TryConvertOADateTime(oValue!, cell.XFormat); //date time offset
					}


					break;
			}
		}

		private bool MoveToNextRecord()
		{
			//if sheet has no index
			if (_noIndex)
			{
				//LogManager.Log(this).Debug("No index");
				return MoveToNextRecordNoIndex();
			}

			//if sheet has index
			if (null == _dbCellAddrs || _dbCellAddrsIndex == _dbCellAddrs.Length || _rowIndex == _lastRowIndex)
				return false;

			_canRead = ReadWorkSheetRow() || !_canRead && _rowIndex > 0;

			//read last row
			if (!_canRead && _dbCellAddrsIndex < (_dbCellAddrs.Length - 1))
			{
				_dbCellAddrsIndex++;
				_cellOffset = FindFirstDataCellOffset((int)_dbCellAddrs[_dbCellAddrsIndex]);
				if (_cellOffset < 0)
					return false;
				_canRead = ReadWorkSheetRow();
			}

			return _canRead;
		}

		private bool MoveToNextRecordNoIndex()
		{
			//seek from current row record to start of cell data where that cell relates to the next row record
			XlsBiffRow rowRecord = _currentRowRecord;

			if (rowRecord == null)
				return false;

			if (rowRecord.RowIndex < _rowIndex)
			{
				_stream.Seek(rowRecord.Offset + rowRecord.Size, SeekOrigin.Begin);
				do
				{
					if (_stream.Position >= _stream.Size)
						return false;

					XlsBiffRecord? record = _stream.Read();
					if (record is XlsBiffEOF)
						return false;

					rowRecord = record as XlsBiffRow ?? null!;
				} while (rowRecord == null || rowRecord.RowIndex < _rowIndex);
			}

			_currentRowRecord = rowRecord;
			//_rowIndex = _currentRowRecord.RowIndex;

			//we have now found the row record for the new row, the we need to seek forward to the first cell record
			XlsBiffBlankCell? cell = null;
			do
			{
				if (_stream.Position >= _stream.Size)
					return false;

				XlsBiffRecord? record = _stream.Read();
				if (record is XlsBiffEOF)
					return false;

				if (record?.IsCell == true)
				{
					XlsBiffBlankCell? candidateCell = record as XlsBiffBlankCell;
					if (candidateCell != null)
					{
						if (candidateCell.RowIndex == _currentRowRecord?.RowIndex)
							cell = candidateCell;
					}
				}
			} while (cell == null);

			_cellOffset = cell.Offset;
			_canRead = ReadWorkSheetRow();


			//read last row
			//if (!_canRead && _rowIndex > 0) _canRead = true;

			//if (!_canRead && _dbCellAddrsIndex < (_dbCellAddrs.Length - 1))
			//{
			//	_dbCellAddrsIndex++;
			//	_cellOffset = findFirstDataCellOffset((int)_dbCellAddrs[_dbCellAddrsIndex]);

			//	_canRead = readWorkSheetRow();
			//}

			return _canRead;
		}

		private void InitializeSheetRead()
		{
			if (_sheetIndex == ResultsCount) return;

			_dbCellAddrs = null!;

			_isFirstRead = false;

			if (_sheetIndex == -1) _sheetIndex = 0;

			XlsBiffIndex idx;

			if (!ReadWorkSheetGlobals(_sheets[_sheetIndex], out idx, out _currentRowRecord))
			{
				//read next sheet
				_sheetIndex++;
				InitializeSheetRead();
				return;
			}


			if (idx == null)
			{
				//no index, but should have the first row record
				_noIndex = true;
			}
			else
			{
				_dbCellAddrs = idx.DbCellAddresses;
				_dbCellAddrsIndex = 0;
				_cellOffset = FindFirstDataCellOffset((int)_dbCellAddrs[_dbCellAddrsIndex]);
				if (_cellOffset < 0)
				{
					throw Fail("Incorrect binary file. INDEX is present, but DBCELL is missing");
				}
			}
		}


		private Exception Fail(string message, Exception? cause = null)
		{
			_isValid = false;

			_file.Close();
			_isClosed = true;

			_workbookData = null!;
			_sheets = null!;
			_stream = null!;
			_globals = null!;
			_encoding = null!;
			_hdr = null!;

			return new FormatException(message, cause);
		}

		private object TryConvertOADateTime(double value, ushort xFormat)
		{
			ushort format;
			if (xFormat < _globals.ExtendedFormats.Count)
			{
				XlsBiffRecord rec = _globals.ExtendedFormats[xFormat];
				switch (rec.ID)
				{
					case BIFFRECORDTYPE.XF_V2:
						format = (ushort)(rec.ReadByte(2) & 0x3F);
						break;
					case BIFFRECORDTYPE.XF_V3:
						if ((rec.ReadByte(3) & 4) == 0)
							return value;
						format = rec.ReadByte(1);
						break;
					case BIFFRECORDTYPE.XF_V4:
						if ((rec.ReadByte(5) & 4) == 0)
							return value;
						format = rec.ReadByte(1);
						break;

					default:
						if ((rec.ReadByte(_globals.Sheets[_globals.Sheets.Count - 1].IsV8 ? 9 : 7) & 4) == 0)
							return value;

						format = rec.ReadUInt16(2);
						break;
				}
			}
			else
			{
				format = xFormat;
			}


			switch (format)
			{
				// numeric built in formats
				case 0: //"General";
				case 1: //"0";
				case 2: //"0.00";
				case 3: //"#,##0";
				case 4: //"#,##0.00";
				case 5: //"\"$\"#,##0_);(\"$\"#,##0)";
				case 6: //"\"$\"#,##0_);[Red](\"$\"#,##0)";
				case 7: //"\"$\"#,##0.00_);(\"$\"#,##0.00)";
				case 8: //"\"$\"#,##0.00_);[Red](\"$\"#,##0.00)";
				case 9: //"0%";
				case 10: //"0.00%";
				case 11: //"0.00E+00";
				case 12: //"# ?/?";
				case 13: //"# ??/??";
				case 0x30: // "##0.0E+0";

				case 0x25: // "_(#,##0_);(#,##0)";
				case 0x26: // "_(#,##0_);[Red](#,##0)";
				case 0x27: // "_(#,##0.00_);(#,##0.00)";
				case 40: // "_(#,##0.00_);[Red](#,##0.00)";
				case 0x29: // "_(\"$\"* #,##0_);_(\"$\"* (#,##0);_(\"$\"* \"-\"_);_(@_)";
				case 0x2a: // "_(\"$\"* #,##0_);_(\"$\"* (#,##0);_(\"$\"* \"-\"_);_(@_)";
				case 0x2b: // "_(\"$\"* #,##0.00_);_(\"$\"* (#,##0.00);_(\"$\"* \"-\"??_);_(@_)";
				case 0x2c: // "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)";
					return value;

				// date formats
				case 14: //this.GetDefaultDateFormat();
				case 15: //"D-MM-YY";
				case 0x10: // "D-MMM";
				case 0x11: // "MMM-YY";
				case 0x12: // "h:mm AM/PM";
				case 0x13: // "h:mm:ss AM/PM";
				case 20: // "h:mm";
				case 0x15: // "h:mm:ss";
				case 0x16: // string.Format("{0} {1}", this.GetDefaultDateFormat(), this.GetDefaultTimeFormat());

				case 0x2d: // "mm:ss";
				case 0x2e: // "[h]:mm:ss";
				case 0x2f: // "mm:ss.0";
					return Helpers.ConvertFromOATime(value);
				case 0x31: // "@";
					return value.ToString();

				default:
					XlsBiffFormatString? fmtString;
					if (_globals.Formats.TryGetValue(format, out fmtString))
					{
						string? fmt = fmtString?.Value;
						if (fmt != null)
					{
						FormatReader formatReader = new FormatReader { FormatString = fmt };
							if (formatReader.IsDateFormatString())
								return Helpers.ConvertFromOATime(value);
					}
					}
					return value;
			}
		}

		private object TryConvertOADateTime(object value, ushort xFormat)
		{
			double dValue;


			if (double.TryParse(value.ToString(), out dValue))
				return TryConvertOADateTime(dValue, xFormat);

			return value;
		}

		/// <summary>Determines whether the workbook uses the BIFF8 format (Excel 97 and later).</summary>
		/// <returns><c>true</c> for BIFF8 (version &gt;= 0x600); otherwise <c>false</c>.</returns>
		public bool IsV8()
		{
			return _version >= 0x600;
		}

		#endregion

		#region IExcelDataReader Members

		/// <summary>Opens the workbook from the supplied stream and reads its global (workbook-level) records.</summary>
		/// <param name="fileStream">The legacy <c>.xls</c> file stream to read.</param>
		public void Initialize(Stream fileStream)
		{
			_file = fileStream;

			ReadWorkBookGlobals();

			// set the sheet index to the index of the first sheet.. this is so that properties such as Name which use m_sheetIndex reflect the first sheet in the file without having to perform a read() operation
			_sheetIndex = 0;
		}

		/// <summary>Reads every worksheet into a <see cref="DataSet"/> without converting OLE-automation date serials.</summary>
		/// <returns>A <see cref="DataSet"/> with one <see cref="DataTable"/> per non-empty worksheet.</returns>
		public DataSet AsDataSet()
		{
			return AsDataSet(false);
		}

		/// <summary>Reads every worksheet into a <see cref="DataSet"/>.</summary>
		/// <param name="convertOADateTime">When <c>true</c>, OLE-automation date serial numbers in date-formatted cells are converted to <see cref="DateTime"/>.</param>
		/// <returns>A <see cref="DataSet"/> with one <see cref="DataTable"/> per non-empty worksheet.</returns>
		public DataSet AsDataSet(bool convertOADateTime)
		{
			if (!_isValid) return new DataSet();

			if (_isClosed) return _workbookData;

			ConvertOaDate = convertOADateTime;
			_workbookData = new DataSet();


			for (int index = 0; index < ResultsCount; index++)
			{
				DataTable? table = ReadWholeWorkSheet(_sheets[index]);

				if (null != table)
					_workbookData.Tables.Add(table);
			}

			_file.Close();
			_isClosed = true;
			_workbookData.AcceptChanges();
			Helpers.FixDataTypes(_workbookData);

			return _workbookData;
		}

		/// <summary>Gets the message describing why the workbook is invalid, if any.</summary>
		public string ExceptionMessage
		{
			get { return _exceptionMessage; }
		}

		/// <summary>Gets the name of the worksheet at the current sheet index, or an empty string when none.</summary>
		public string Name
		{
			get
			{
				if (null != _sheets && _sheets.Count > 0)
					return _sheets[_sheetIndex].Name;
				return string.Empty;
			}
		}

		/// <summary>Gets whether the workbook was opened successfully and is readable.</summary>
		public bool IsValid
		{
			get { return _isValid; }
		}

		/// <summary>Closes the reader and the underlying file stream.</summary>
		public void Close()
		{
			_file.Close();
			_isClosed = true;
		}

		/// <summary>Gets the zero-based index of the current row within the active worksheet.</summary>
		public int Depth
		{
			get { return _rowIndex; }
		}

		/// <summary>Gets the number of worksheets in the workbook.</summary>
		public int ResultsCount
		{
			get { return _globals.Sheets.Count; }
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
			if (_sheetIndex >= (ResultsCount - 1)) return false;

			_sheetIndex++;

			_isFirstRead = true;

			return true;
		}

		/// <summary>Advances to the next row of the current worksheet.</summary>
		/// <returns><c>true</c> if a row was read; otherwise <c>false</c>.</returns>
		public bool Read()
		{
			if (!_isValid) return false;

			if (_isFirstRead) InitializeSheetRead();

			return MoveToNextRecord();
		}

		/// <summary>Gets the number of columns in the current worksheet.</summary>
		public int FieldCount
		{
			get { return _maxCol; }
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
		/// <returns>The date/time value, or <see cref="DateTime.MinValue"/> if the cell is null.</returns>
		public DateTime GetDateTime(int i)
		{
			if (IsDBNull(i)) return DateTime.MinValue;

			// requested change: 3
			object val = _cellsValues[i];

			if (val is DateTime)
			{
				// if the value is already a datetime.. return it without further conversion
				return (DateTime)val;
			}

			// otherwise proceed with conversion attempts
			string valString = val.ToString()!;
			double dVal;

			try
			{
				dVal = double.Parse(valString);
			}
			catch (FormatException)
			{
				return DateTime.Parse(valString);
			}

			return DateTime.FromOADate(dVal);
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

		#region IExcelDataReader Members

		/// <summary>Gets or sets whether the first row of each sheet supplies the column names instead of data.</summary>
		public bool IsFirstRowAsColumnNames
		{
			get { return _isFirstRowAsColumnNames; }
			set { _isFirstRowAsColumnNames = value; }
		}

		/// <summary>Gets or sets whether numeric cells with a date number-format are converted to <see cref="DateTime"/>.</summary>
		public bool ConvertOaDate { get; set; }

		/// <summary>Gets the option controlling how strictly the BIFF stream is interpreted while reading.</summary>
		public ReadOption ReadOption
		{
			get { return _readOption; }
		}

		#endregion
	}
}
