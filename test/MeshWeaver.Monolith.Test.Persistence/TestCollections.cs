using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeshWeaver.Monolith.Test.Persistence;

[CollectionDefinition("KernelTests", DisableParallelization = true)]
public class KernelTestsCollection;

[CollectionDefinition("FileSystemWatcherTests", DisableParallelization = true)]
public class FileSystemWatcherTestsCollection;
