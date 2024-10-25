using System.Reactive.Linq;
using MeshWeaver.Activities;
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
            data.FromConfigurableDataSource(
                "reference",
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
            data.FromConfigurableDataSource(
                "transactional",
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
            data.FromConfigurableDataSource(
                "computed",
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
                data.FromPartitionedHubs<TransactionalDataAddress>(
                        nameof(TransactionalData),
                        c =>
                            c.WithType<TransactionalData>(td => new TransactionalDataAddress(td.Year, td.BusinessUnit))
                    )
                    .FromPartitionedHubs<ComputedDataAddress>(
                        nameof(ComputedData),
                        c => c.WithType<ComputedData>(cd => new(cd.Year, cd.BusinessUnit))
                    )
                    .FromHub(
                        new ReferenceDataAddress(),
                        dataSource =>
                            dataSource.WithType<BusinessUnit>().WithType<LineOfBusiness>()
                    )
                    .FromConfigurableDataSource(
                        nameof(ActivityLog),
                        dataSource => dataSource.WithType<ActivityLog>(t => t)
                    )
            )
            .AddImport(import =>
                import.WithFormat(
                    CashflowImportFormat,
                    format => format.WithAutoMappings().WithImportFunction(ImportFunction)
                )
            );
        

    private static async IAsyncEnumerable<object> ImportFunction(
        ImportRequest request,
        IDataSet dataSet,
        IWorkspace workspace
    )
    {
        var businessUnits = await workspace.GetStream<BusinessUnit>().FirstAsync();
        var partitions = businessUnits.Select(bu => new TransactionalDataAddress(2024, bu.SystemName)).ToArray();

        var transactionalData = await partitions
            .ToAsyncEnumerable()
            .SelectAwait(async p =>
                await workspace.GetStream(
                    new PartitionedWorkspaceReference<InstanceCollection>(p, new CollectionReference(typeof(TransactionalData).FullName))).FirstAsync())
            .ToArrayAsync();

        foreach (var x in transactionalData.SelectMany(x =>
                     x.Value
                         .Instances
                         .Values
                         .Cast<TransactionalData>()
                         .Select(t => new ComputedData(
                             t.Id,
                             2024,
                             t.LoB,
                             t.BusinessUnit,
                             t.Value * 2
                         ))))
        {
            yield return x;
        }
    }
}
