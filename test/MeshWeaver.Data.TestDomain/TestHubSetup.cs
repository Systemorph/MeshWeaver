using System.Linq;
using System.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.TestDomain;

public static class TestHubSetup
{
    public static MessageHubConfiguration ConfigureReferenceDataModel(
        this MessageHubConfiguration configuration
    ) =>
        configuration.AddData(data =>
            data.AddSource(
                dataSource =>
                    dataSource
                        .WithType<LineOfBusiness>(t =>
                            t.WithInitialData(TestData.LinesOfBusiness)
                        )
                        .WithType<BusinessUnit>(t =>
                            t.WithInitialData(TestData.BusinessUnits)
                        )
            )
        );

    public static MessageHubConfiguration ConfigureTransactionalModel(
        this MessageHubConfiguration configuration,
        Address address
    )
    {
        // Parse the address Id which has format "Year-BusinessUnit"
        var parts = address.Id.Split('-');
        var year = int.Parse(parts[0]);
        var businessUnit = parts[1];
        return configuration.AddData(data =>
            data.AddSource(
                dataSource =>
                    dataSource.WithType<TransactionalData>(t =>
                        t.WithInitialData(
                            TestData.TransactionalData.Where(v =>
                                v.BusinessUnit == businessUnit && v.Year == year
                            )
                        )
                    )
            )
        );
    }

    public static MessageHubConfiguration ConfigureComputedModel(
        this MessageHubConfiguration configuration
    ) =>
        configuration.AddData(data =>
            data.AddSource(
                dataSource => dataSource.WithType<ComputedData>(t => t)
            )
        );

    /// <summary>
    /// The shared test router. The IMPORT route lives with the Import module's own tests now
    /// (MeshWeaver.Import left the platform): a SHARED test helper must not bind a module, or
    /// every consumer of the helper needs the module's assembly on disk.
    /// </summary>
    public static MessageHubConfiguration ConfigureDataRouter(this MessageHubConfiguration config)
        => config.WithRoutes(forward =>
            forward
                .RouteAddressToHostedHub(nameof(ReferenceDataAddress), c => c.ConfigureReferenceDataModel())
                .RouteAddressToHostedHub(nameof(TransactionalData), c =>
                    c.ConfigureTransactionalModel(c.Address))
                .RouteAddressToHostedHub(nameof(ComputedDataAddress), c => c.ConfigureComputedModel())
        );
}
