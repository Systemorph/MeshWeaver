namespace MeshWeaver.DataSetReader.Excel.Utils
{
    /// <summary>
    /// Controls how strictly the BIFF stream of a legacy <c>.xls</c> workbook is interpreted while reading.
    /// </summary>
    public enum ReadOption
    {
        /// <summary>Honour the worksheet index strictly, stopping when the recorded layout ends.</summary>
        Strict,
        /// <summary>Read leniently, tolerating records that fall outside the strictly-expected layout.</summary>
        Loose
    }
}
