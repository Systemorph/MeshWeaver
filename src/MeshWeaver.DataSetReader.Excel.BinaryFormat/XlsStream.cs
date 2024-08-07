// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents an Excel file stream
	/// </summary>
	internal class XlsStream
	{
		private readonly XlsFat _fat;
		private XlsFat _minifat;
		private readonly Stream _fileStream;
		private readonly XlsHeader _hdr;
		private readonly uint _startSector;
		private readonly bool _isMini;
		private readonly XlsRootDirectory _rootDir;

		public XlsStream(XlsHeader hdr, uint startSector, bool isMini, XlsRootDirectory rootDir)
		{
			_fileStream = hdr.FileStream;
			_fat = hdr.FAT;
			_hdr = hdr;
			_startSector = startSector;
			_isMini = isMini;
			_rootDir = rootDir;

			CalculateMiniFat(rootDir);

		}

		public void CalculateMiniFat(XlsRootDirectory rootDir)
		{
			_minifat = _hdr.GetMiniFAT(rootDir);
		}

		/// <summary>
		/// Returns offset of first stream sector
		/// </summary>
		public uint BaseOffset
		{
			get { return (uint)((_startSector + 1) * _hdr.SectorSize); }
		}

		/// <summary>
		/// Returns number of first stream sector
		/// </summary>
		public uint BaseSector
		{
			get { return (_startSector); }
		}

		/// <summary>
		/// Reads stream data from file
		/// </summary>
		/// <returns>Stream data</returns>
		public byte[] ReadStream()
		{
			uint sector = _startSector, prevSector = 0;
			int sectorSize = _isMini ? _hdr.MiniSectorSize : _hdr.SectorSize;
			XlsFat fat = _isMini ? _minifat : _fat;
			long offset = 0;
			if (_isMini && _rootDir != null)
			{
				offset = (_rootDir.RootEntry.StreamFirstSector + 1)*_hdr.SectorSize;
			}

			byte[] buff = new byte[sectorSize];
			byte[] ret;

			using (MemoryStream ms = new MemoryStream(sectorSize * 8))
			{
				lock (_fileStream)
				{
					do
					{
						if (prevSector == 0 || (sector - prevSector) != 1)
						{
							uint adjustedSector = _isMini ? sector : sector + 1; //standard sector is + 1 because header is first
							_fileStream.Seek(adjustedSector * sectorSize + offset, SeekOrigin.Begin);
						}
							
						prevSector = sector;
						_fileStream.Read(buff, 0, sectorSize);
						ms.Write(buff, 0, sectorSize);
					} while ((sector = fat.GetNextSector(sector)) != (uint)FATMARKERS.FAT_EndOfChain);
				}

				ret = ms.ToArray();
			}

			return ret;
		}
	}
}
