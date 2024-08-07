using MeshWeaver.DataStructures;

namespace MeshWeaver.Import;

public record FileReadStorageConfiguration(object Id, Func<FileDescription, IDataSet> ReadFile);

public record FileDescription(object FileSource, string FullPath, string Format);
