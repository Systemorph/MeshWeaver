using MeshWeaver.DataStructures;

namespace MeshWeaver.Import;

/// <summary>
/// Associates a storage identifier with a function that reads a described file into a data set.
/// </summary>
/// <param name="Id">Identifier of the storage this configuration applies to.</param>
/// <param name="ReadFile">Function that reads a <see cref="FileDescription"/> into an <c>IDataSet</c>.</param>
public record FileReadStorageConfiguration(object Id, Func<FileDescription, IDataSet> ReadFile);

/// <summary>
/// Describes a file to be read: its source, full path and format.
/// </summary>
/// <param name="FileSource">The source the file originates from (e.g. a storage or collection handle).</param>
/// <param name="FullPath">The full path to the file.</param>
/// <param name="Format">The file format / MIME type used to select a reader.</param>
public record FileDescription(object FileSource, string FullPath, string Format);
