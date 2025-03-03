// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

using MeshWeaver.DataSetReader.Excel.Utils;

namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents basic BIFF record
	/// Base class for all BIFF record types
	/// </summary>
	internal class XlsBiffRecord
	{
		private readonly byte[] _bytes;
		protected readonly ExcelBinaryReader reader;
		protected int readoffset;

		protected XlsBiffRecord(byte[] bytes, uint offset, ExcelBinaryReader reader)
		{
			if (bytes.Length - offset < 4)
				throw new ArgumentException(Errors.ErrorBIFFRecordSize);
			_bytes = bytes;
			this.reader = reader;
			readoffset = (int)(4 + offset);

			//Set readOption to loose to not cause exception here (sql reporting services)
			if (reader.ReadOption == ReadOption.Strict)
			{
				if (bytes.Length < offset + Size)
					throw new ArgumentException(Errors.ErrorBIFFBufferSize);
			}
			
		}

		internal byte[] Bytes
		{
			get { return _bytes; }
		}

		internal int Offset
		{
			get { return readoffset - 4; }
		}

		/// <summary>
		/// Returns type ID of this entry
		/// </summary>
		public BIFFRECORDTYPE ID
		{
			get { return (BIFFRECORDTYPE)BitConverter.ToUInt16(_bytes, Offset); }
		}

		/// <summary>
		/// Returns data size of this entry
		/// </summary>
		public ushort RecordSize
		{
			get { return BitConverter.ToUInt16(_bytes, readoffset - 2); }
		}

		/// <summary>
		/// Returns whole size of structure
		/// </summary>
		public int Size
		{
			get { return 4 + RecordSize; }
		}

		/// <summary>
		/// Returns record at specified offset
		/// </summary>
		/// <param name="bytes">byte array</param>
		/// <param name="offset">position in array</param>
		/// <param name="reader"></param>
		/// <returns></returns>
		public static XlsBiffRecord GetRecord(byte[] bytes, uint offset, ExcelBinaryReader reader)
		{
			uint id = BitConverter.ToUInt16(bytes, (int)offset);
			//Console.WriteLine("GetRecord {0}", (BIFFRECORDTYPE)ID);
			switch ((BIFFRECORDTYPE)id)
			{
				case BIFFRECORDTYPE.BOF_V2:
				case BIFFRECORDTYPE.BOF_V3:
				case BIFFRECORDTYPE.BOF_V4:
				case BIFFRECORDTYPE.BOF:
					return new XlsBiffBOF(bytes, offset, reader);
				case BIFFRECORDTYPE.EOF:
					return new XlsBiffEOF(bytes, offset, reader);
				case BIFFRECORDTYPE.INTERFACEHDR:
					return new XlsBiffInterfaceHdr(bytes, offset, reader);

				case BIFFRECORDTYPE.SST:
					return new XlsBiffSST(bytes, offset, reader);

				case BIFFRECORDTYPE.INDEX:
					return new XlsBiffIndex(bytes, offset, reader);
				case BIFFRECORDTYPE.ROW:
					return new XlsBiffRow(bytes, offset, reader);
				case BIFFRECORDTYPE.DBCELL:
					return new XlsBiffDbCell(bytes, offset, reader);

                case BIFFRECORDTYPE.BOOLERR:
                case BIFFRECORDTYPE.BOOLERR_OLD:
				case BIFFRECORDTYPE.BLANK:
				case BIFFRECORDTYPE.BLANK_OLD:
					return new XlsBiffBlankCell(bytes, offset, reader);
				case BIFFRECORDTYPE.MULBLANK:
					return new XlsBiffMulBlankCell(bytes, offset, reader);
				case BIFFRECORDTYPE.LABEL:
				case BIFFRECORDTYPE.LABEL_OLD:
				case BIFFRECORDTYPE.RSTRING:
					return new XlsBiffLabelCell(bytes, offset, reader);
				case BIFFRECORDTYPE.LABELSST:
					return new XlsBiffLabelSSTCell(bytes, offset, reader);
				case BIFFRECORDTYPE.INTEGER:
				case BIFFRECORDTYPE.INTEGER_OLD:
					return new XlsBiffIntegerCell(bytes, offset, reader);
				case BIFFRECORDTYPE.NUMBER:
				case BIFFRECORDTYPE.NUMBER_OLD:
					return new XlsBiffNumberCell(bytes, offset, reader);
				case BIFFRECORDTYPE.RK:
					return new XlsBiffRKCell(bytes, offset, reader);
				case BIFFRECORDTYPE.MULRK:
					return new XlsBiffMulRKCell(bytes, offset, reader);
				case BIFFRECORDTYPE.FORMULA:
				case BIFFRECORDTYPE.FORMULA_OLD:
					return new XlsBiffFormulaCell(bytes, offset, reader);
                case BIFFRECORDTYPE.FORMAT_V23:
                case BIFFRECORDTYPE.FORMAT:
					return new XlsBiffFormatString(bytes, offset, reader);
				case BIFFRECORDTYPE.STRING:
					return new XlsBiffFormulaString(bytes, offset, reader);
				case BIFFRECORDTYPE.CONTINUE:
					return new XlsBiffContinue(bytes, offset, reader);
				case BIFFRECORDTYPE.DIMENSIONS:
					return new XlsBiffDimensions(bytes, offset, reader);
				case BIFFRECORDTYPE.BOUNDSHEET:
					return new XlsBiffBoundSheet(bytes, offset, reader);
				case BIFFRECORDTYPE.WINDOW1:
					return new XlsBiffWindow1(bytes, offset, reader);
				case BIFFRECORDTYPE.CODEPAGE:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.FNGROUPCOUNT:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.RECORD1904:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.BOOKBOOL:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.BACKUP:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.HIDEOBJ:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.USESELFS:
					return new XlsBiffSimpleValueRecord(bytes, offset, reader);
				case BIFFRECORDTYPE.UNCALCED:
					return new XlsBiffUncalced(bytes, offset, reader);
				case BIFFRECORDTYPE.QUICKTIP:
					return new XlsBiffQuickTip(bytes, offset, reader);

				default:
					return new XlsBiffRecord(bytes, offset, reader);
			}
		}

		public bool IsCell
		{
			get
			{//FORMULA / Blank / MulBlank / RK / MulRk / BoolErr / Number / LabelSst
				bool isCell = false;
				switch (ID)
				{
					case BIFFRECORDTYPE.FORMULA:
					case BIFFRECORDTYPE.BLANK:
					case BIFFRECORDTYPE.MULBLANK:
					case BIFFRECORDTYPE.RK:
					case BIFFRECORDTYPE.MULRK:
					case BIFFRECORDTYPE.BOOLERR:
					case BIFFRECORDTYPE.NUMBER:
					case BIFFRECORDTYPE.LABELSST:
						isCell = true;
						break;
				}

				return isCell;
			}
		}

		public byte ReadByte(int offset)
		{
			return Buffer.GetByte(_bytes, readoffset + offset);
		}

		public ushort ReadUInt16(int offset)
		{
			return BitConverter.ToUInt16(_bytes, readoffset + offset);
		}

		public uint ReadUInt32(int offset)
		{
			return BitConverter.ToUInt32(_bytes, readoffset + offset);
		}

		public ulong ReadUInt64(int offset)
		{
			return BitConverter.ToUInt64(_bytes, readoffset + offset);
		}

		public short ReadInt16(int offset)
		{
			return BitConverter.ToInt16(_bytes, readoffset + offset);
		}

		public int ReadInt32(int offset)
		{
			return BitConverter.ToInt32(_bytes, readoffset + offset);
		}

		public long ReadInt64(int offset)
		{
			return BitConverter.ToInt64(_bytes, readoffset + offset);
		}

		public byte[] ReadArray(int offset, int size)
		{
			byte[] tmp = new byte[size];
			Buffer.BlockCopy(_bytes, readoffset + offset, tmp, 0, size);
			return tmp;
		}

		public float ReadFloat(int offset)
		{
			return BitConverter.ToSingle(_bytes, readoffset + offset);
		}

		public double ReadDouble(int offset)
		{
			return BitConverter.ToDouble(_bytes, readoffset + offset);
		}
	}
}
