using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Collection definition for kernel tests.
/// Kernel tests need exclusive access to kernel resources.
/// </summary>
[CollectionDefinition("KernelTests", DisableParallelization = true)]
public class KernelTestsCollection;

/// <summary>
/// Collection definition for file system watcher tests.
/// These tests use file system watchers that can interfere with each other.
/// </summary>
[CollectionDefinition("FileSystemWatcherTests", DisableParallelization = true)]
public class FileSystemWatcherTestsCollection;
