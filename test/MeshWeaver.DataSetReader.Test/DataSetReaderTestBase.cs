using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit.Abstractions;

namespace MeshWeaver.DataSetReader.Test;

public abstract class DataSetReaderTestBase(ITestOutputHelper toh) : TestBase(toh)
{
    protected ITestFileStorageService FileStorageService;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        FileStorageService = new TestFileStorageService();
    }
}
