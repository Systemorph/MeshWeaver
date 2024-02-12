using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using Xunit.Abstractions;

namespace OpenSmc.DataSetReader.Test;

public abstract class DataSetReaderTestBase : TestBase
{
    protected IDataSetReaderVariable DataSetReaderVariable;
    protected ITestFileStorageService FileStorageService;

    protected DataSetReaderTestBase(ITestOutputHelper toh)
    : base(toh)
    {
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var dataSetReadingService = ServiceProvider.GetService<IDataSetReadingService>();
        DataSetReaderVariable = new DataSetReaderVariable(dataSetReadingService);
        FileStorageService = new TestFileStorageService();
    }
}