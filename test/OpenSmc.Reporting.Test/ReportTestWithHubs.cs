using FluentAssertions;
using OpenSmc.Hub.Fixture;
using OpenSmc.TestDomain.SimpleData;
using Xunit.Abstractions;
using OpenSmc.Messaging;
using OpenSmc.Data;
using OpenSmc.Data.TestDomain;
using OpenSmc.Activities;

namespace OpenSmc.Reporting.Test;

//public static class PivotRegistryExtensions
//{
//    public static MessageHubConfiguration AddPivot(this MessageHubConfiguration conf)
//    {
//        return conf.WithServices(services => services
//            .AddArithmetics()
//            .RegisterScopes());
//    }

//    // TODO V10: move to pivot project (08.02.2024, Ekaterina Mishina)
//    public static MessageHubConfiguration AddPivot2(this MessageHubConfiguration conf)
//    {
//        return conf
//            .AddArithmetics()
//            .AddDataCubes()
//            .AddScopesDataCubes()
//            .AddScopes();
//    }
//}

public static class DataExtensions
{
    public static DataSource WithInitialData<T>(this DataSource dataSource, IEnumerable<T> data) where T : class =>
        dataSource.WithType<T>(t => t.WithInitialData(data));

    public static MessageHubConfiguration ConfigureDataForReport(this MessageHubConfiguration parent)
        => parent.WithHostedHub(
            new ReportDataAddress(parent.Address),
            configuration => configuration
                .AddData(data => data
                    .FromConfigurableDataSource
                    (
                        "DataForReport",
                        dataSource => dataSource.WithInitialData(ValueWithHierarchicalDimension.Data)
                    )
                )
        );

    public static MessageHubConfiguration ConfigureReportingHub(this MessageHubConfiguration parent)
        => parent.WithHostedHub(new ReportingAddress(), config => config
            .AddReporting(data => data
                    .FromHub(new ReportDataAddress(parent.Address),
                        dataSource => dataSource
                            .WithType<ValueWithHierarchicalDimension>()
                    ),
                reportConfig => reportConfig
            ));
}

public record ReportingAddress;

public class ReportTestWithHubs(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
                .ConfigureDataForReport()
                .ConfigureReportingHub()
            ;
    }

    [Fact]
    public async Task SimpleReport()
    {
        var client = GetClient();
        var reportRequest = new ReportRequest();

        // act
        var reportResponse = await client.AwaitResponse(reportRequest, o => o.WithTarget(new ReportingAddress()));

        // assert
        reportResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        //var data = ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce();
        // TODO V10: move this to report config (configure report hub) (05.03.2024, Ekaterina Mishina)
        //var gridOptions = PivotFactory.ForDataCubes(data)
        //    .WithQuerySource(new StaticDataFieldQuerySource())
        //    .SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
        //    .ToTable()
        //    .WithOptions(rm => rm.HideRowValuesForDimension("DimA", x => x.ForLevel(1)))
        //    .WithOptions(o => o.AutoHeight())
        //    .Execute();

        //await gridOptions.Verify("HierarchicalDimensionHideAggregation.json");
    }
    
}

