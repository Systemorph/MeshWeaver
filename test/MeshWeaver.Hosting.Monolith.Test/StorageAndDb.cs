using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Builders;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class StorageAndDbContainerTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlContainer postgresContainer = new PostgreSqlBuilder()
        .WithImage(PostgreSqlBuilder.PostgreSqlImage)
        .WithDatabase("TestDb")
        .WithUsername("postgres")
        .WithPassword("password")
        .WithPortBinding(PostgreSqlBuilder.PostgreSqlPort, true)
        .Build();

    private readonly IContainer azuriteContainer = new ContainerBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithPortBinding(10000, 10000) // Blob storage
        .WithPortBinding(10001, 10001) // Queue storage
        .WithPortBinding(10002, 10002) // Table storage
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
        .Build();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Start containers
        await postgresContainer.StartAsync();
        await azuriteContainer.StartAsync();
    }

    public override async Task DisposeAsync()
    {
        // Cleanup containers
        if (azuriteContainer is not null)
        {
            await azuriteContainer.DisposeAsync();
        }

        if (postgresContainer is not null)
        {
            await postgresContainer.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    // Helper properties to access connection strings
    protected string PostgresConnectionString => postgresContainer.GetConnectionString();
    protected string AzuriteConnectionString =>
        $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
}
