using OpenSmc.DataStructures;

namespace OpenSmc.Import;

public record FileReadStorageConfiguration(object Id, Func<FileDescription, IDataSet> ReadFile);

public record FileDescription(object FileSource, string FullPath, string Format);
