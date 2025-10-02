using System;
using System.Net.NetworkInformation;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace MeshWeaver.Hosting.Monolith.Test;


public static class ContainerExtensions
{
    private static int GetAvailablePort(int startingPort = 10000)
    {
        var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
        var connections = ipProperties.GetActiveTcpConnections();
        var listeners = ipProperties.GetActiveTcpListeners();

        for (int port = startingPort; port < startingPort + 1000; port++)
        {
            bool isAvailable = true;
            foreach (var connection in connections)
            {
                if (connection.LocalEndPoint.Port == port)
                {
                    isAvailable = false;
                    break;
                }
            }

            if (isAvailable)
            {
                foreach (var listener in listeners)
                {
                    if (listener.Port == port)
                    {
                        isAvailable = false;
                        break;
                    }
                }
            }

            if (isAvailable)
                return port;
        }

        throw new InvalidOperationException("No available ports found");
    }

    public static (IContainer container, string connectionString) CreateAzurite()
    {
        var blobPort = GetAvailablePort(10000);
        var queuePort = GetAvailablePort(blobPort + 1);
        var tablePort = GetAvailablePort(queuePort + 1);

        var container = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithPortBinding(blobPort, 10000) // Blob storage
            .WithPortBinding(queuePort, 10001) // Queue storage
            .WithPortBinding(tablePort, 10002) // Table storage
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(10000))
            .WithCleanUp(true)
            .Build();

        var connectionString = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:{blobPort}/devstoreaccount1;QueueEndpoint=http://127.0.0.1:{queuePort}/devstoreaccount1;TableEndpoint=http://127.0.0.1:{tablePort}/devstoreaccount1;";

        return (container, connectionString);
    }

    public static string AzuriteConnectionString =>
    "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";

    public static IContainer Azurite() => new ContainerBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithPortBinding(10000, 10000) // Blob storage
        .WithPortBinding(10001, 10001) // Queue storage
        .WithPortBinding(10002, 10002) // Table storage
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(10000))
        .WithCleanUp(true) // Ensure container is cleaned up
        .Build();

    public static PostgreSqlContainer Postgres() => new PostgreSqlBuilder()
        .WithImage(PostgreSqlBuilder.PostgreSqlImage)
        .WithDatabase("TestDb")
        .WithUsername("postgres")
        .WithPassword("password")
        .WithPortBinding(PostgreSqlBuilder.PostgreSqlPort, true)
        .Build();
}
