using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Reporting.Builder;
using MeshWeaver.TestDomain.SimpleData;
using MeshWeaver.Utils;
using Xunit;

namespace MeshWeaver.Reporting.Test;

public class ReportDataCubeTest : HubTestBase
{
    public ReportDataCubeTest(ITestOutputHelper toh)
        : base(toh) { }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    dataSource =>
                        dataSource
                            .WithType<TestHierarchicalDimensionA>(type =>
                                type.WithInitialData(TestHierarchicalDimensionA.Data)
                            )
                            .WithType<TestHierarchicalDimensionB>(type =>
                                type.WithInitialData(TestHierarchicalDimensionB.Data)
                            )
                            .WithType<ValueWithHierarchicalDimension>(type =>
                                type.WithInitialData(ValueWithHierarchicalDimension.Data)
                            )
                            .WithType<ValueWithAggregateByHierarchicalDimension>(type =>
                                type.WithInitialData(ValueWithAggregateByHierarchicalDimension.Data)
                            )
                            .WithType<ValueWithMixedDimensions>(type =>
                                type.WithInitialData(ValueWithMixedDimensions.Data)
                            )
                            .WithType<ValueWithTwoHierarchicalDimensions>(type =>
                                type.WithInitialData(ValueWithTwoHierarchicalDimensions.Data)
                            )
                )
            );
    }


    [Theory]
    [MemberData(nameof(ReportDataCubeCases))]
    public async Task ReportDataCubeWithGridOptions<T>(
        string fileName,
        IEnumerable<IDataCube<T>> data,
        Func<
            DataCubePivotBuilder<IDataCube<T>, T, T, T>,
            DataCubeReportBuilder<IDataCube<T>, T, T, T>
        > toReportBuilder
    )
    {
        var initialPivotBuilder = GetHost().GetWorkspace()
            .ForDataCubes(data);

        var reportBuilder = toReportBuilder(initialPivotBuilder);

        var gridOptions = await GetModelAsync(reportBuilder);
        await gridOptions.Verify(fileName);
    }

    public static IEnumerable<object[]> ReportDataCubeCases()
    {
        yield return new ReportDataCubeTestCase<ValueWithHierarchicalDimension>(
            "HierarchicalDimensionHideAggregation.json",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .ToTable()
                    .WithOptions(rm => rm.HideRowValuesForDimension("DimA", x => x.ForLevel(1)))
        );
        yield return new ReportDataCubeTestCase<ValueWithHierarchicalDimension>(
            "HierarchicalDimensionHideAggregation21.json",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .ToTable()
                    .WithOptions(rm =>
                        rm.HideRowValuesForDimension(
                            "DimA",
                            x => x.ForLevel(1).ForSystemName("A1", "A2")
                        )
                    )
        );
        yield return new ReportDataCubeTestCase<ValueWithHierarchicalDimension>(
            "HierarchicalDimensionHideAggregation22.json",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .ToTable()
                    .WithOptions(rm =>
                        rm.HideRowValuesForDimension(
                            "DimA",
                            x => x.ForLevel(1).ForSystemName("A1").ForSystemName("A2")
                        )
                    )
        );
        yield return new ReportDataCubeTestCase<ValueWithHierarchicalDimension>(
            "HierarchicalDimensionHideAggregation23.json",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .ToTable()
                    .WithOptions(rm =>
                        rm.HideRowValuesForDimension(
                            "DimA",
                            x => x.ForSystemName("A1", "A2").ForLevel(1)
                        )
                    )
        );
        yield return new ReportDataCubeTestCase<ValueWithHierarchicalDimension>(
            "HierarchicalDimensionHideAggregation24.json",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .ToTable()
                    .WithOptions(rm => rm.HideRowValuesForDimension("DimA", x => x.ForLevel(0, 1)))
        );
        yield return new ReportDataCubeTestCase<ValueWithMixedDimensions>(
            "HierarchicalDimensionHideAggregation31.json",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                        nameof(ValueWithMixedDimensions.DimA),
                        nameof(ValueWithMixedDimensions.DimD)
                    )
                    .ToTable()
                    .WithOptions(rm => rm.HideRowValuesForDimension("DimA"))
        );
        yield return new ReportDataCubeTestCase<ValueWithMixedDimensions>(
            "HierarchicalDimensionHideAggregation32.json",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                        nameof(ValueWithMixedDimensions.DimA),
                        nameof(ValueWithMixedDimensions.DimD)
                    )
                    .ToTable()
                    .WithOptions(rm =>
                        rm.HideRowValuesForDimension("DimA", x => x.ForLevel(0, 1, 2))
                    )
        );
        yield return new ReportDataCubeTestCase<ValueWithMixedDimensions>(
            "HierarchicalDimensionHideAggregation33.json",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                        nameof(ValueWithMixedDimensions.DimA),
                        nameof(ValueWithMixedDimensions.DimD)
                    )
                    .ToTable()
                    .WithOptions(rm =>
                        rm.HideRowValuesForDimension(
                            "DimA",
                            x => x.ForLevel(0).ForLevel(1).ForLevel(2)
                        )
                    )
        );
        yield return new ReportDataCubeTestCase<ValueWithMixedDimensions>(
            "HierarchicalDimensionHideAggregation4.json",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                        nameof(ValueWithMixedDimensions.DimA),
                        nameof(ValueWithMixedDimensions.DimD)
                    )
                    .ToTable()
                    .WithOptions(rm =>
                        rm.HideRowValuesForDimension("DimA", x => x.ForLevel(1))
                            .HideRowValuesForDimension("DimD", x => x.ForSystemName("D1"))
                    )
        );
    }

    private class ReportDataCubeTestCase<T>
    {
        private readonly IEnumerable<IDataCube<T>> data;
        private readonly Func<
            DataCubePivotBuilder<IDataCube<T>, T, T, T>,
            DataCubeReportBuilder<IDataCube<T>, T, T, T>
        > pivotBuilder;
        private readonly string benchmarkFile;

        public static implicit operator object[](ReportDataCubeTestCase<T> testCase) =>
            new object[] { testCase.benchmarkFile, testCase.data, testCase.pivotBuilder };

        public ReportDataCubeTestCase(
            string benchmarkFile,
            IEnumerable<IDataCube<T>> data,
            Func<
                DataCubePivotBuilder<IDataCube<T>, T, T, T>,
                DataCubeReportBuilder<IDataCube<T>, T, T, T>
            > pivotBuilder
        )
        {
            this.data = data;
            this.pivotBuilder = pivotBuilder;
            this.benchmarkFile = benchmarkFile;
        }
    }

    protected async Task<object> GetModelAsync<T, TIntermediate, TAggregate>(
        PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder
    )
    {
        return await pivotBuilder.ToTable().WithOptions(o => o.AutoHeight()).Execute().FirstAsync();
    }

    private protected async Task<object> GetModelAsync<T, TIntermediate, TAggregate>(
        ReportBuilder<T, TIntermediate, TAggregate> reportBuilder
    )
    {
        return await reportBuilder.WithOptions(o => o.AutoHeight()).Execute().FirstAsync();
    }

    protected async Task<object> GetModelAsync<TElement, TIntermediate, TAggregate>(
        DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate> pivotBuilder
    )
    {
        return await pivotBuilder.ToTable().WithOptions(o => o.AutoHeight()).Execute().FirstAsync();
    }

    private async Task<object> GetModelAsync<T, TIntermediate, TAggregate>(
        DataCubeReportBuilder<IDataCube<T>, T, TIntermediate, TAggregate> reportBuilder
    )
    {
        return await reportBuilder.WithOptions(o => o.AutoHeight()).Execute().FirstAsync();
    }
}
