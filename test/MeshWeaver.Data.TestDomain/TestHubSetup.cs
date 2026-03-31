using System.Linq;
using System.Threading;
using MeshWeaver.DataStructures;
using MeshWeaver.Import;
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

    public const string CashflowImportFormat = nameof(CashflowImportFormat);

    public static MessageHubConfiguration ConfigureImportRouter(this MessageHubConfiguration config)
        => config.WithRoutes(forward =>
            forward
                .RouteAddressToHostedHub(nameof(ReferenceDataAddress), c => c.ConfigureReferenceDataModel())
                .RouteAddressToHostedHub(nameof(TransactionalData), c =>
                    c.ConfigureTransactionalModel(c.Address))
                .RouteAddressToHostedHub(nameof(ComputedDataAddress), c => c.ConfigureComputedModel())
                .RouteAddressToHostedHub(nameof(ImportAddress), c => c.ConfigureImportHub())
        );
    public static MessageHubConfiguration ConfigureImportHub(
        this MessageHubConfiguration config
    ) =>
        config
            .AddData(data =>
                data.AddPartitionedHubSource<Address>(
                        c =>
                            c.WithType<TransactionalData>(td => TransactionalDataAddress.Create(td.Year, td.BusinessUnit))
                                .InitializingPartitions(TransactionalDataAddress.Create(2024, "1"), TransactionalDataAddress.Create(2024, "2"))
                    )
                    .AddPartitionedHubSource<Address>(
                        c => c.WithType<ComputedData>(cd => ComputedDataAddress.Create(cd.Year, cd.BusinessUnit))
                            .InitializingPartitions(ComputedDataAddress.Create(2024, "1"), ComputedDataAddress.Create(2024, "2"))
                    )
                    .AddHubSource(
                        ReferenceDataAddress.Create(),
                        dataSource =>
                            dataSource.WithType<BusinessUnit>().WithType<LineOfBusiness>()
                    )
                    .AddSource(
                        dataSource => dataSource.WithType<ActivityLog>(t => t)
                    )
            )
            .AddImport(import =>
                import.WithFormat(
                    CashflowImportFormat,
                    format => format.WithAutoMappings().WithImportFunction(ImportFunction)
                )
            );


    private static EntityStore ImportFunction(
        ImportRequest request,
        IDataSet dataSet,
        IWorkspace workspace,
        EntityStore store,
        CancellationToken ct
    )
    {
        var transactionalData = store.GetData<TransactionalData>();

        var instances = transactionalData.Select(t => new ComputedData(
                    t.Id,
                    2024,
                    t.LoB,
                    t.BusinessUnit,
                    t.Value * 2
                )).ToArray();
        return workspace.AddInstances(store, instances);
    }
}
