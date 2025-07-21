using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.DataSetReader.Test;

public abstract class DataSetReaderTestBase(ITestOutputHelper toh) : TestBase(toh)
{
    protected ITestFileStorageService FileStorageService = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        FileStorageService = new TestFileStorageService();
    }
}
