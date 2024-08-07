// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

using System.Runtime.CompilerServices;
using MeshWeaver.DataSetReader.Excel.Utils;

namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents a BIFF stream
	/// </summary>
	internal class XlsBiffStream : XlsStream
	{
		private readonly ExcelBinaryReader _reader;
		private readonly byte[] _bytes;
		private readonly int _size;
		private int _offset;

		public XlsBiffStream(XlsHeader hdr, uint streamStart, bool isMini, XlsRootDirectory rootDir, ExcelBinaryReader reader)
			: base(hdr, streamStart, isMini, rootDir)
		{
			_reader = reader;
			_bytes = ReadStream();
			_size = _bytes.Length;
			_offset = 0;

		}

		/// <summary>
		/// Returns size of BIFF stream in bytes
		/// </summary>
		public int Size
		{
			get { return _size; }
		}

		/// <summary>
		/// Returns current position in BIFF stream
		/// </summary>
		public int Position
		{
			get { return _offset; }
		}
		
		/// <summary>
		/// Sets stream pointer to the specified offset
		/// </summary>
		/// <param name="offset">Offset value</param>
		/// <param name="origin">Offset origin</param>
		[MethodImpl(MethodImplOptions.Synchronized)]
		public void Seek(int offset, SeekOrigin origin)
		{
			switch (origin)
			{
				case SeekOrigin.Begin:
					_offset = offset;
					break;
				case SeekOrigin.Current:
					_offset += offset;
					break;
				case SeekOrigin.End:
					_offset = _size - offset;
					break;
			}
			if (_offset < 0)
				throw new ArgumentOutOfRangeException(string.Format("{0} On offset={1}", Errors.ErrorBIFFIlegalBefore, offset));
			if (_offset > _size)
				throw new ArgumentOutOfRangeException(string.Format("{0} On offset={1}", Errors.ErrorBIFFIlegalAfter, offset));
		}

		/// <summary>
		/// Reads record under cursor and advances cursor position to next record
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.Synchronized)]
		public XlsBiffRecord Read()
		{
			XlsBiffRecord rec = XlsBiffRecord.GetRecord(_bytes, (uint)_offset, _reader);
			_offset += rec.Size;
			if (_offset > _size)
				return null;
			return rec;
		}

		/// <summary>
		/// Reads record at specified offset, does not change cursor position
		/// </summary>
		/// <param name="offset"></param>
		/// <returns></returns>
		public XlsBiffRecord ReadAt(int offset)
		{
			XlsBiffRecord rec = XlsBiffRecord.GetRecord(_bytes, (uint)offset, _reader);

			//choose ReadOption.Loose to skip this check (e.g. sql reporting services)
			if (_reader.ReadOption == ReadOption.Strict)
			{
				if (_offset + rec.Size > _size)
					return null;
			}
			
			return rec;
		}
	}
}
