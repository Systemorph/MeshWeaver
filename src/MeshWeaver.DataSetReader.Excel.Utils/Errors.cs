namespace MeshWeaver.DataSetReader.Excel.Utils
{
    /// <summary>
    /// Human-readable error messages produced while parsing the compound-document / BIFF structure of a legacy <c>.xls</c> workbook.
    /// </summary>
    public static class Errors
    {
        /// <summary>Neither the <c>Workbook</c> nor the <c>Book</c> stream could be found in the file.</summary>
        public const string ErrorStreamWorkbookNotFound = "Error: Neither stream 'Workbook' nor 'Book' was found in file.";
        /// <summary>The workbook directory entry is not a stream.</summary>
        public const string ErrorWorkbookIsNotStream = "Error: Workbook directory entry is not a Stream.";
        /// <summary>The workbook globals stream contained invalid data.</summary>
        public const string ErrorWorkbookGlobalsInvalidData = "Error reading Workbook Globals - Stream has invalid data.";
        /// <summary>A referenced sector does not exist in the FAT (File Allocation Table).</summary>
        public const string ErrorFATBadSector = "Error reading as FAT table : There's no such sector in FAT.";
        /// <summary>A stream could not be read from the FAT area.</summary>
        public const string ErrorFATRead = "Error reading stream from FAT area.";
        /// <summary>The file signature (magic number) is invalid.</summary>
        public const string ErrorHeaderSignature = "Error: Invalid file signature.";
        /// <summary>The byte order specified in the file header is invalid.</summary>
        public const string ErrorHeaderOrder = "Error: Invalid byte order specified in header.";
        /// <summary>The buffer is smaller than the minimum BIFF record size.</summary>
        public const string ErrorBIFFRecordSize = "Error: Buffer size is less than minimum BIFF record size.";
        /// <summary>The buffer is smaller than the BIFF entry length.</summary>
        public const string ErrorBIFFBufferSize = "BIFF Stream error: Buffer size is less than entry length.";
        /// <summary>An attempt was made to seek before the start of the BIFF stream.</summary>
        public const string ErrorBIFFIlegalBefore = "BIFF Stream error: Moving before stream start.";
        /// <summary>An attempt was made to seek past the end of the BIFF stream.</summary>
        public const string ErrorBIFFIlegalAfter = "BIFF Stream error: Moving after stream end.";

        /// <summary>The supplied array is too small to hold the directory entries.</summary>
        public const string ErrorDirectoryEntryArray = "Directory Entry error: Array is too small.";
    }
}
