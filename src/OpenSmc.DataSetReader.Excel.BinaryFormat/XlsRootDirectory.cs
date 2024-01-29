// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/

using System.Collections.ObjectModel;

namespace OpenSmc.DataSetReader.Excel.BinaryFormat
{
	/// <summary>
	/// Represents Root Directory in file
	/// </summary>
	internal class XlsRootDirectory
	{
		private readonly List<XlsDirectoryEntry> _entries;
		private readonly XlsDirectoryEntry _root;

		/// <summary>
		/// Creates Root Directory catalog from XlsHeader
		/// </summary>
		/// <param name="hdr">XlsHeader object</param>
		public XlsRootDirectory(XlsHeader hdr)
		{
			XlsStream stream = new XlsStream(hdr, hdr.RootDirectoryEntryStart, false, null);
			byte[] array = stream.ReadStream();
			List<XlsDirectoryEntry> entries = new List<XlsDirectoryEntry>();
			for (int i = 0; i < array.Length; i += XlsDirectoryEntry.Length)
			{
				byte[] tmp = new byte[XlsDirectoryEntry.Length];
				Buffer.BlockCopy(array, i, tmp, 0, tmp.Length);
				entries.Add(new XlsDirectoryEntry(tmp, hdr));
			}
			_entries = entries;
			for (int i = 0; i < entries.Count; i++)
			{
				XlsDirectoryEntry entry = entries[i];

				if (_root == null && entry.EntryType == STGTY.STGTY_ROOT)
					_root = entry;
				if (entry.ChildSid != (uint)FATMARKERS.FAT_FreeSpace)
					entry.Child = entries[(int)entry.ChildSid];
				if (entry.LeftSiblingSid != (uint)FATMARKERS.FAT_FreeSpace)
					entry.LeftSibling = entries[(int)entry.LeftSiblingSid];
				if (entry.RightSiblingSid != (uint)FATMARKERS.FAT_FreeSpace)
					entry.RightSibling = entries[(int)entry.RightSiblingSid];
			}
			stream.CalculateMiniFat(this);
		}

		/// <summary>
		/// Returns all entries in Root Directory
		/// </summary>
		public ReadOnlyCollection<XlsDirectoryEntry> Entries
		{
			get { return _entries.AsReadOnly(); }
		}

		/// <summary>
		/// Returns the Root Entry
		/// </summary>
		public XlsDirectoryEntry RootEntry
		{
			get { return _root; }
		}

		/// <summary>
		/// Searches for first matching entry by its name
		/// </summary>
		/// <param name="entryName">String name of entry</param>
		/// <returns>Entry if found, null otherwise</returns>
		public XlsDirectoryEntry FindEntry(string entryName)
		{
			return _entries.FirstOrDefault(e => e.EntryName == entryName);
		}
	}
}
