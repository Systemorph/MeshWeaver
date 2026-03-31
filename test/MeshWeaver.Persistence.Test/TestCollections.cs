using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeshWeaver.Persistence.Test;

[CollectionDefinition("KernelTests", DisableParallelization = true)]
public class KernelTestsCollection;

[CollectionDefinition("FileSystemWatcherTests", DisableParallelization = true)]
public class FileSystemWatcherTestsCollection;
