using System.IO.Compression;

namespace MeshWeaver.DataSetReader.Excel.Utils
{
	public class ZipWorker : IDisposable
	{
		#region Members and Properties
		Dictionary<string, Stream> zipStreams = new Dictionary<string, Stream>();
		private bool disposed;

		/// <summary>
		/// Gets a value indicating whether this instance is valid.
		/// </summary>
		/// <value><c>true</c> if this instance is valid; otherwise, <c>false</c>.</value>
		public bool IsValid { get; private set; }

		/// <summary>
		/// Gets the exception message.
		/// </summary>
		/// <value>The exception message.</value>
		public string ExceptionMessage { get; private set; } = string.Empty;

		#endregion

		/// <summary>
		/// Extracts the specified zip file stream.
		/// </summary>
		/// <param name="fileStream">The zip file stream.</param>
		/// <returns></returns>
		public bool Extract(Stream fileStream)
		{
			if (null == fileStream) return false;

			IsValid = true;
			ZipArchive? zipFile = null;

			try
			{
				zipFile = new ZipArchive(fileStream);
				foreach (var zipArchiveEntry in zipFile.Entries)
					ExtractZipEntry(zipArchiveEntry);
			}
			catch (Exception ex)
			{
				IsValid = false;
				ExceptionMessage = ex.ToString();
			}
			finally
			{
				fileStream.Close();
				if (null != zipFile) zipFile.Dispose();
			}

			return IsValid;
		}

		private void ExtractZipEntry(ZipArchiveEntry entry)
		{
			if (string.IsNullOrEmpty(entry.FullName))
				return;

			if (entry.FullName.EndsWith("/")) //Directory
				return;

			if (!entry.FullName.StartsWith("xl/")) //Not used
				return;

			using (Stream inputStream = entry.Open())
			{
				var memStream = new MemoryStream((int)entry.Length);
				inputStream.CopyTo(memStream);
				memStream.Seek(0, SeekOrigin.Begin);
				zipStreams.Add(entry.FullName, memStream);
			}
		}

		private Stream? GetStreamOrDefault(string filePath)
		{
			return zipStreams.TryGetValue(filePath, out var stream) ? stream : null;
		}

		public Stream? GetSharedStringsStream() => GetStreamOrDefault("xl/sharedStrings.xml");
		public Stream? GetStylesStream() => GetStreamOrDefault("xl/styles.xml");
		public Stream? GetWorkbookStream() => GetStreamOrDefault("xl/workbook.xml");
		public Stream? GetWorkbookRelsStream() => GetStreamOrDefault("xl/_rels/workbook.xml.rels");
		public Stream? GetWorksheetStream(string sheetPath)
		{
			//its possible sheetPath starts with /xl. in this case trim the /xl
			if (sheetPath.StartsWith("/xl/"))
				sheetPath = sheetPath.Substring(4);

			return GetStreamOrDefault($"xl/{sheetPath}");
		}

		#region IDisposable Members

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		private void Dispose(bool disposing)
		{
			// Check to see if Dispose has already been called.
			if (!this.disposed)
			{
				foreach (var zipStream in zipStreams)
				{
					zipStream.Value.Dispose();
				}
				zipStreams.Clear();

				disposed = true;
			}
		}

		~ZipWorker()
		{
			Dispose(false);
		}

		#endregion
	}
}
