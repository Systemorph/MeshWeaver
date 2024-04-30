using OpenSmc.DataStructures;
using OpenSmc.Import;
using OpenSmc.Messaging;

namespace OpenSmc.Data.TestDomain;

public static class TestHubSetup
{
    public static MessageHubConfiguration ConfigureReferenceDataModel(
        this MessageHubConfiguration parent
    ) =>
        parent.WithHostedHub(
            new ReferenceDataAddress(parent.Address),
            configuration =>
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
                )
        );

    public static MessageHubConfiguration ConfigureTransactionalModel(
        this MessageHubConfiguration parent,
        int year,
        params string[] businessUnits
    ) =>
        businessUnits.Aggregate(
            parent,
            (c, businessUnit) =>
                c.WithHostedHub(
                    new TransactionalDataAddress(year, businessUnit, parent.Address),
                    configuration =>
                        configuration.AddData(data =>
                            data.FromConfigurableDataSource(
                                "transactional",
                                dataSource =>
                                    dataSource.WithType<TransactionalData>(t =>
                                        t.WithInitialData(
                                            TestData.TransactionalData.Where(v =>
                                                v.BusinessUnit == businessUnit && v.Year == year
                                            )
                                        )
                                    )
                            )
                        )
                )
        );

    public static MessageHubConfiguration ConfigureComputedModel(
        this MessageHubConfiguration parent,
        int year,
        params string[] businessUnits
    ) =>
        businessUnits.Aggregate(
            parent,
            (c, businessUnit) =>
                c.WithHostedHub(
                    new ComputedDataAddress(year, businessUnit, parent.Address),
                    configuration =>
                        configuration.AddData(data =>
                            data.FromConfigurableDataSource(
                                "computed",
                                dataSource => dataSource.WithType<ComputedData>(t => t)
                            )
                        )
                )
        );

    public const string CashflowImportFormat = nameof(CashflowImportFormat);

    public static MessageHubConfiguration ConfigureImportHub(
        this MessageHubConfiguration parent,
        int year,
        params string[] businessUnits
    ) =>
        parent.WithHostedHub(
            new ImportAddress(year, parent.Address),
            config =>
                config
                    .AddData(data =>
                        data.FromPartitionedHubs(
                                nameof(TransactionalData),
                                c =>
                                    c.InitializingPartitions(
                                            businessUnits.Select(bu => new TransactionalDataAddress(
                                                year,
                                                bu,
                                                parent.Address
                                            ))
                                        )
                                        .WithType<TransactionalData>(
                                            i => new TransactionalDataAddress(
                                                i.Year,
                                                i.BusinessUnit,
                                                parent.Address
                                            )
                                        )
                            )
                            .FromPartitionedHubs(
                                nameof(ComputedData),
                                c =>
                                    c.InitializingPartitions(
                                            businessUnits.Select(bu => new ComputedDataAddress(
                                                year,
                                                bu,
                                                parent.Address
                                            ))
                                        )
                                        .WithType<ComputedData>(i => new ComputedDataAddress(
                                            i.Year,
                                            i.BusinessUnit,
                                            parent.Address
                                        ))
                            )
                            .FromHub(
                                new ReferenceDataAddress(parent.Address),
                                dataSource =>
                                    dataSource.WithType<BusinessUnit>().WithType<LineOfBusiness>()
                            )
                    )
                    .AddImport(import =>
                        import.WithFormat(
                            CashflowImportFormat,
                            format => format.WithAutoMappings().WithImportFunction(ImportFunction)
                        )
                    )
        );

    private static IEnumerable<object> ImportFunction(
        ImportRequest request,
        IDataSet dataSet,
        IMessageHub hub,
        WorkspaceState state
    )
    {
        var transactionalData = state.GetData<TransactionalData>();
        return transactionalData.Select(t => new ComputedData(
            t.Id,
            2024,
            t.LoB,
            t.BusinessUnit,
            t.Value * 2
        ));
    }
}
