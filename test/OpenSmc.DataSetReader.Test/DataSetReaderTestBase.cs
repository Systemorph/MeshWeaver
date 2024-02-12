using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using Xunit.Abstractions;

namespace OpenSmc.DataSetReader.Test;

public abstract class DataSetReaderTestBase(ITestOutputHelper toh) : TestBase(toh)
{
    protected ITestFileStorageService FileStorageService;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        FileStorageService = new TestFileStorageService();
    }
}