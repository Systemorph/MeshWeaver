using MeshWeaver.Import.Configuration;

namespace MeshWeaver.Import;

public static class ExcelImportExtensions
{
    /// <summary>
    /// Imports entities from an Excel stream using the provided configuration.
    /// </summary>
    /// <typeparam name="T">The entity type to import</typeparam>
    /// <param name="configuration">The import configuration (not used directly, but provides context)</param>
    /// <param name="stream">The Excel file stream</param>
    /// <param name="excelConfig">The Excel import configuration</param>
    /// <param name="entityBuilder">Function to build an entity from property dictionary</param>
    /// <returns>Enumerable of imported entities</returns>
    public static IEnumerable<T> ImportExcel<T>(
        this ImportConfiguration configuration,
        Stream stream,
        ExcelImportConfiguration excelConfig,
        Func<Dictionary<string, object?>, T> entityBuilder) where T : class
    {
        var importer = new ConfiguredExcelImporter<T>(entityBuilder);
        return importer.Import(stream, excelConfig.Name, excelConfig);
    }

    /// <summary>
    /// Imports entities from an Excel file using the provided configuration.
    /// </summary>
    /// <typeparam name="T">The entity type to import</typeparam>
    /// <param name="configuration">The import configuration (not used directly, but provides context)</param>
    /// <param name="filePath">Path to the Excel file</param>
    /// <param name="excelConfig">The Excel import configuration</param>
    /// <param name="entityBuilder">Function to build an entity from property dictionary</param>
    /// <returns>Enumerable of imported entities</returns>
    public static IEnumerable<T> ImportExcel<T>(
        this ImportConfiguration configuration,
        string filePath,
        ExcelImportConfiguration excelConfig,
        Func<Dictionary<string, object?>, T> entityBuilder) where T : class
    {
        var importer = new ConfiguredExcelImporter<T>(entityBuilder);
        return importer.Import(filePath, excelConfig);
    }
}
