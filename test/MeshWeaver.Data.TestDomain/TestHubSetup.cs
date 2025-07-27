using System.Linq;
using MeshWeaver.Data;
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
        TransactionalDataAddress address
    ) =>
        configuration.AddData(data =>
            data.AddSource(
                dataSource =>
                    dataSource.WithType<TransactionalData>(t =>
                        t.WithInitialData(
                            TestData.TransactionalData.Where(v =>
                                v.BusinessUnit == address.BusinessUnit && v.Year == address.Year
                            )
                        )
                    )
            )
        );

    public static MessageHubConfiguration ConfigureComputedModel(
        this MessageHubConfiguration configuration,
        ComputedDataAddress address
    ) =>
        configuration.AddData(data =>
            data.AddSource(
                dataSource => dataSource.WithType<ComputedData>(t => t)
            )
        );

    public const string CashflowImportFormat = nameof(CashflowImportFormat);

    public static MessageHubConfiguration ConfigureImportHub(
        this MessageHubConfiguration config,
        ImportAddress address
    ) =>
        config
            .AddData(data =>
                data.AddPartitionedHubSource<TransactionalDataAddress>(
                        c =>
                            c.WithType<TransactionalData>(td => new TransactionalDataAddress(td.Year, td.BusinessUnit))
                    )
                    .AddPartitionedHubSource<ComputedDataAddress>(
                        c => c.WithType<ComputedData>(cd => new(cd.Year, cd.BusinessUnit))
                    )
                    .AddHubSource(
                        new ReferenceDataAddress(),
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
        EntityStore store
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
