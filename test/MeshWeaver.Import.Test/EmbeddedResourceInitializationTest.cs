using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Import.Test.Domain;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Import.Test;

public class EmbeddedResourceInitializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddImport()
            .AddData(data => data
                .FromEmbeddedResource(new EmbeddedResource(GetType().Assembly, "Files.categories.csv"), m => m.WithType<Category>())
                .FromEmbeddedResource(new EmbeddedResource(GetType().Assembly, "Files.products.csv"), m => m.WithType<Product>())
                .FromEmbeddedResource(new EmbeddedResource(GetType().Assembly, "Files.suppliers.csv"), m => m.WithType<Supplier>())
            );
    }


    [Fact]
    public async Task BasicInitialization()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var categories = await workspace
            .GetObservable<Category>()
            .FirstAsync(x => x?.Count > 0);
        categories.Should().NotBeEmpty();
    }
}
