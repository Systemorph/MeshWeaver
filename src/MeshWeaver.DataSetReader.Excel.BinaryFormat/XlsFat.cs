// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

using MeshWeaver.DataSetReader.Excel.Utils;

namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents Excel file FAT table
	/// </summary>
	internal class XlsFat
	{
		private readonly List<uint> _fat;

		private XlsFat(int sectors)
		{
			_fat = new List<uint>(sectors);
		}

		/// <summary>
		/// Constructs FAT table from list of sectors
		/// </summary>
		/// <param name="hdr">XlsHeader</param>
		/// <param name="sectors">Sectors list</param>
		public static XlsFat Create(XlsHeader hdr, IReadOnlyList<uint> sectors)
		{
			int sectorsForFAT = sectors.Count;
			int sizeOfSector = hdr.SectorSize;
			uint prevSector = 0;

			//calc offset of stream . If mini stream then find mini stream container stream
			//long offset = 0;
			//if (rootDir != null)
			//	offset = isMini ? (hdr.MiniFatFirstSector + 1) * hdr.SectorSize : 0;

			var buff = new byte[sizeOfSector];
			var file = hdr.FileStream;

            using var ms = new MemoryStream(sizeOfSector * sectorsForFAT);
            lock (file)
            {
                lock (file)
                {
                    foreach (var sector in sectors)
                    {
                        if (prevSector == 0 || (sector - prevSector) != 1)
                            file.Seek((sector + 1) * sizeOfSector, SeekOrigin.Begin);
                        prevSector = sector;

                        int bytesRead;
                        var totalBytesRead = 0;
                        while (totalBytesRead < sizeOfSector && (bytesRead = file.Read(buff, totalBytesRead, sizeOfSector - totalBytesRead)) > 0)
                        {
                            totalBytesRead += bytesRead;
                        }
                        ms.Write(buff, 0, totalBytesRead);
                    }
                }
            }
            ms.Seek(0, SeekOrigin.Begin);
            using var rd = new BinaryReader(ms);
            var sectors1 = (int) ms.Length/4;
            var result = new XlsFat(sectors1);
            for (var i = 0; i < sectors1; i++)
                result._fat.Add(rd.ReadUInt32());
            return result;
        }

		/// <summary>
		/// Returns next data sector using FAT
		/// </summary>
		/// <param name="sector">Current data sector</param>
		/// <returns>Next data sector</returns>
		public uint GetNextSector(uint sector)
		{
			if (_fat.Count <= sector)
				throw new ArgumentException(Errors.ErrorFATBadSector);
			var value = _fat[(int)sector];
			if (value == (uint)FATMARKERS.FAT_FatSector || value == (uint)FATMARKERS.FAT_DifSector)
				throw new InvalidOperationException(Errors.ErrorFATRead);
			return value;
		}
	}
}
