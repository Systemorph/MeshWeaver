using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Arithmetics;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Fixture;
using MeshWeaver.Json.Assertions;
using MeshWeaver.Messaging;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;
using MeshWeaver.TestDomain;
using MeshWeaver.TestDomain.Cubes;
using MeshWeaver.TestDomain.SimpleData;
using MeshWeaver.Utils;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Pivot.Test;

public class PivotTest(ITestOutputHelper output) : HubTestBase(output)
{
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
                            .WithType<Currency>(type => type.WithInitialData(Currency.Data))
                            .WithType<AmountType>(type => type.WithInitialData(AmountType.Data))
                            .WithType<Country>(type => type.WithInitialData(Country.Data))
                            .WithType<LineOfBusiness>(type =>
                                type.WithInitialData(LineOfBusiness.Data)
                            )
                            .WithType<Scenario>(type => type.WithInitialData(Scenario.Data))
                            .WithType<Company>(type => type.WithInitialData(Company.Data))
                            .WithType<Dim1>(type => type)
                            .WithType<Dim2>(type => type)
                            .WithType<Split>(type => type.WithInitialData(Split.Data))
                )
            );
    }

    JsonSerializerOptions Options => GetHost().JsonSerializerOptions;


    [Theory]
    [MemberData(nameof(TestCases))]
    public async Task Reports<T, TAggregate>(
        string fileName,
        IEnumerable<T> data,
        Func<PivotBuilder<T, T, T>, PivotBuilder<T, TAggregate, TAggregate>> builder
    )
    {
        var initial = (GetHost().GetWorkspace()).Pivot(data);

        var pivotBuilder = builder(initial);

        var model = await GetModel(pivotBuilder);
        await model.JsonShouldMatch(Options, $"{fileName}.json");
    }

    [Theory]
    [MemberData(nameof(TestCasesCount))]
    public async Task ReportsCounts<T>(
        string fileName,
        IEnumerable<T> data,
        Func<PivotBuilder<T, T, T>, PivotBuilder<T, int, int>> builder
    )
    {
        var initial = (GetHost().GetWorkspace()).Pivot(data);

        var pivotBuilder = builder(initial);

        var model = await GetModel(pivotBuilder);
        await model.JsonShouldMatch(Options, $"{fileName}.json");
    }


    [Theory]
    [MemberData(nameof(DataCubeTestCases))]
    public async Task DataCubeReports<TElement>(
        string fileName,
        IEnumerable<IDataCube<TElement>> data,
        Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>
        > pivotBuilder
    )
    {
        await ExecuteDataCubeTest(fileName, data, pivotBuilder);
    }

    [Theory]
    [MemberData(nameof(DataCubeCountTestCases))]
    public async Task DataCubeCountReports<TElement>(
        string fileName,
        IEnumerable<IDataCube<TElement>> data,
        Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, int, int>
        > pivotBuilder
    )
    {
        await ExecuteDataCubeCountTest(fileName, data, pivotBuilder);
    }

    [Theory]
    [MemberData(nameof(DataCubeAverageTestCases))]
    public async Task DataCubeAverageReports<TElement>(
        string fileName,
        IEnumerable<IDataCube<TElement>> data,
        Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, (TElement sum, int count), TElement>
        > pivotBuilder
    )
    {
        await ExecuteDataCubeAverageTest(fileName, data, pivotBuilder);
    }


    [Fact(Skip = "Not clear what the use case should be for this")]
    public async Task NullQuerySourceShouldFlatten()
    {
        PivotModel? qs = null;
        var exception = await Record.ExceptionAsync(
            async () =>
                qs = await GetHost().GetWorkspace()
                    .Pivot(ValueWithHierarchicalDimension.Data.ToDataCube())
                    .SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .Execute()
                    .FirstAsync()
        );
        Assert.Null(exception);
        Assert.NotNull(qs);

        var flattened = await (GetHost().GetWorkspace())
            .Pivot(ValueWithHierarchicalDimension.Data.ToDataCube())
            .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>())
            .SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
            .Execute()
            .FirstAsync();

        qs!.Columns.Should().BeEquivalentTo(flattened.Columns);
        // excluding display names, as they differ due to missing data source
        qs.Rows.Should().BeEquivalentTo(flattened.Rows, o => o.Excluding(r => r.RowGroup!.DisplayName));
        qs.HasRowGrouping.Should().Be(flattened.HasRowGrouping);
    }


    public static IEnumerable<object[]> DataCubeCountTestCases()
    {
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Count())
                    .SliceRowsBy(nameof(Currency))
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount1",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Count())
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount2",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Count())
                    .SliceRowsBy(nameof(Currency))
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount3",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Count())
                    .SliceRowsBy(nameof(Country))
                    .SliceRowsBy(nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount4",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Count())
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceRowsBy(nameof(Country))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionCount",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Count())
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
    }

    public static IEnumerable<object[]> DataCubeAverageTestCases()
    {
        yield return new DataCubeTestCase<
            CashflowElement,
            (CashflowElement sum, int count),
            CashflowElement
        >(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyAverage",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Average((s, c) => ArithmeticOperations.Divide(s, c)))
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<
            CashflowElement,
            (CashflowElement sum, int count),
            CashflowElement
        >(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionAverage",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b =>
                b.WithAggregation(a => a.Average((s, c) => ArithmeticOperations.Divide(s, c)))
                    .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
    }

    public static IEnumerable<object[]> DataCubeTestCases()
    {
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrency",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceRowsByAggregateByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceRowsByAggregateByDimension2",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Currency))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceColumnsByAggregateByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Currency))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceColumnsByAggregateByDimension2",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country)).SliceColumnsBy(nameof(Currency))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceColumnsByAggregateByDimension3",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Currency)).SliceRowsBy(nameof(Country))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTriceByRowOneDimensionSingleCurrency",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness), nameof(Split))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTriceByRowOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness), nameof(Split))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedOnceByRowOnceByColumnOneDimensionSingleCurrency",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country)).SliceColumnsBy(nameof(LineOfBusiness))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedOnceByRowOnceByColumnOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country)).SliceColumnsBy(nameof(LineOfBusiness))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnOneDimensionSingleCurrency",
            CashflowFactory
                .GenerateEquallyWeightedAllPopulatedSingleCurrency()
                .ToDataCube()
                .RepeatOnce(),
            b => b.SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnThreeDimensions",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Country), nameof(LineOfBusiness), nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnThreeDimensions2",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b =>
                b.SliceColumnsBy(nameof(Country), nameof(LineOfBusiness))
                    .SliceColumnsBy(nameof(AmountType))
        );
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeTransposed",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.Transpose<double>()
        );
        yield return new DataCubeTestCase<TwoDimValue, TwoDimValue>(
            "NullGroupSliceByRowTwoDimensionsTotalsBug",
            TwoDimValue.Data.Take(3).ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Dim2), nameof(Dim1))
        );
        yield return new DataCubeTestCase<TwoDimValue, TwoDimValue>(
            "NullGroupSliceByRowSliceByColumn",
            TwoDimValue.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Dim1)).SliceColumnsBy(nameof(Dim2))
        );
        yield return new DataCubeTestCase<
            ValueWithHierarchicalDimension,
            ValueWithHierarchicalDimension
        >(
            "HierarchicalDimension2",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
        );
        yield return new DataCubeTestCase<
            ValueWithHierarchicalDimension,
            ValueWithHierarchicalDimension
        >(
            "HierarchicalDimensionOptionsA12",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .WithHierarchicalDimensionOptions(o =>
                        o.LevelMin<TestHierarchicalDimensionA>(1)
                    )
        );
        yield return new DataCubeTestCase<
            ValueWithHierarchicalDimension,
            ValueWithHierarchicalDimension
        >(
            "HierarchicalDimensionOptionsA22",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .WithHierarchicalDimensionOptions(o =>
                        o.LevelMin<TestHierarchicalDimensionA>(1)
                            .LevelMax<TestHierarchicalDimensionA>(1)
                    )
        );
        yield return new DataCubeTestCase<
            ValueWithHierarchicalDimension,
            ValueWithHierarchicalDimension
        >(
            "HierarchicalDimensionOptionsAFlat2",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                    .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>())
        );
        yield return new DataCubeTestCase<
            ValueWithTwoAggregateByHierarchicalDimensions,
            ValueWithTwoAggregateByHierarchicalDimensions
        >(
            "HierarchicalDimensionTwoAggregateBy",
            ValueWithTwoAggregateByHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b
        );
        yield return new DataCubeTestCase<
            ValueWithTwoAggregateByHierarchicalDimensions,
            ValueWithTwoAggregateByHierarchicalDimensions
        >(
            "HierarchicalDimensionTwoAggregateBySliceByColumn",
            ValueWithTwoAggregateByHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(ValueWithHierarchicalDimension.DimA))
        );
        yield return new DataCubeTestCase<
            ValueWithTwoHierarchicalDimensions,
            ValueWithTwoHierarchicalDimensions
        >(
            "HierarchicalDimensionTwo2",
            ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                    nameof(ValueWithHierarchicalDimension.DimA),
                    nameof(ValueWithTwoHierarchicalDimensions.DimB)
                )
        );
        yield return new DataCubeTestCase<
            ValueWithTwoHierarchicalDimensions,
            ValueWithTwoHierarchicalDimensions
        >(
            "HierarchicalDimensionTwoByColumns",
            ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceColumnsBy(
                    nameof(ValueWithHierarchicalDimension.DimA),
                    nameof(ValueWithTwoHierarchicalDimensions.DimB)
                )
        );
        yield return new DataCubeTestCase<
            ValueWithTwoHierarchicalDimensions,
            ValueWithTwoHierarchicalDimensions
        >(
            "HierarchicalDimensionSliceColumnsByB",
            ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(ValueWithTwoHierarchicalDimensions.DimB))
        );
        yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>(
            "HierarchicalDimensionMix1",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimA))
                    .SliceColumnsBy(nameof(ValueWithMixedDimensions.DimD))
        );
        yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>(
            "HierarchicalDimensionMix2",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                    nameof(ValueWithMixedDimensions.DimA),
                    nameof(ValueWithMixedDimensions.DimD)
                )
        );
        yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>(
            "HierarchicalDimensionMix3",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b =>
                b.SliceRowsBy(
                    nameof(ValueWithMixedDimensions.DimD),
                    nameof(ValueWithMixedDimensions.DimA)
                )
        );
    }


    public static IEnumerable<object[]> TestCasesCount()
    {
        yield return new TestCase<SimpleAccounting, int>(
            "AutorenderedBasicCount",
            SimpleAccountingFactory.GetData(3),
            x => x.WithAggregation(a => a.Count())
        );

        yield return new TestCase<CashflowElement, int>(
            "GroupedByRowsAndByColumnsCount",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x =>
                x.WithAggregation(a => a.Count())
                    .GroupColumnsBy(y => y.LineOfBusiness)
                    .GroupRowsBy(y => y.AmountType)
        );
    }

    public static IEnumerable<object[]> TestCases()
    {
        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "AutorenderedBasic",
            SimpleAccountingFactory.GetData(3),
            x => x
        );

        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "AutorenderedTransposedBasic",
            SimpleAccountingFactory.GetData(1),
            x => x.Transpose<double>()
        );

        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "AutorenderedTransposedMultipleBasic",
            SimpleAccountingFactory.GetData(3),
            x => x.Transpose<double>()
        );

        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "BasicWithCustomRowDefinitions",
            SimpleAccountingFactory.GetData(3),
            rb =>
                rb.GroupRowsBy(o => new RowGroup(
                    o.SystemName,
                    o.DisplayName,
                    PivotConst.AutomaticEnumerationPivotGrouperName
                ))
        );

        yield return new TestCase<SimpleAccountingNamed, SimpleAccountingNamed>(
            "BasicWithCustomRowDefinitions2",
            SimpleAccountingFactory.GetData<SimpleAccountingNamed>(3),
            rb => rb
        );

        yield return new TestCase<SimpleAccountingNamed, SimpleAccountingNamed>(
            "TransposedWithCustomGroupDefinitions",
            SimpleAccountingFactory.GetData<SimpleAccountingNamed>(3),
            rb => rb.Transpose<double>()
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupRowsByDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupRowsBy(y => new RowGroup(y.AmountType!, y.AmountType!, nameof(y.AmountType)))
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupRowsByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupRowsBy(y => new RowGroup(y.AmountType!, y.AmountType!, nameof(y.AmountType)))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupRowsByDimensionPropertySingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupRowsBy(y => y.AmountType)
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupRowsByDimensionProperty",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupRowsBy(y => y.AmountType)
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupColumnsByDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x =>
                x.GroupColumnsBy(y => new ColumnGroup(
                    y.AmountType!,
                    y.AmountType!,
                    nameof(y.AmountType)
                ))
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupColumnsByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x =>
                x.GroupColumnsBy(y => new ColumnGroup(
                    y.AmountType!,
                    y.AmountType!,
                    nameof(y.AmountType)
                ))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupColumnsByDimensionPropertySingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.Transpose<double>().GroupColumnsBy(y => y.AmountType)
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupColumnsByDimensionProperty",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.Transpose<double>().GroupColumnsBy(y => y.AmountType)
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCubeSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x =>
                x.GroupRowsBy(y => new RowGroup(
                        y.LineOfBusiness!,
                        y.LineOfBusiness!,
                        nameof(y.LineOfBusiness)
                    ))
                    .GroupRowsBy(y => new RowGroup(
                        y.AmountType!,
                        y.AmountType!,
                        nameof(y.AmountType)
                    ))
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCube",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x =>
                x.GroupRowsBy(y => new RowGroup(
                        y.LineOfBusiness!,
                        y.LineOfBusiness!,
                        nameof(y.LineOfBusiness)
                    ))
                    .GroupRowsBy(y => new RowGroup(
                        y.AmountType!,
                        y.AmountType!,
                        nameof(y.AmountType)
                    ))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCubeTransposedSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x =>
                x.Transpose<double>()
                    .GroupColumnsBy(y => new ColumnGroup(
                        y.LineOfBusiness!,
                        y.LineOfBusiness!,
                        nameof(y.LineOfBusiness)
                    ))
                    .GroupColumnsBy(y => new ColumnGroup(
                        y.AmountType!,
                        y.AmountType!,
                        nameof(y.AmountType)
                    ))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCubeTransposed",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x =>
                x.Transpose<double>()
                    .GroupColumnsBy(y => new ColumnGroup(
                        y.LineOfBusiness!,
                        y.LineOfBusiness!,
                        nameof(y.LineOfBusiness)
                    ))
                    .GroupColumnsBy(y => new ColumnGroup(
                        y.AmountType!,
                        y.AmountType!,
                        nameof(y.AmountType)
                    ))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupedByRowsAndByColumnsSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupColumnsBy(y => y.LineOfBusiness).GroupRowsBy(y => y.AmountType)
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupedByRowsAndByColumns",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupColumnsBy(y => y.LineOfBusiness).GroupRowsBy(y => y.AmountType)
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TransposedGroupedByRowsAndByColumnsSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x =>
                x.Transpose<double>()
                    .GroupColumnsBy(y => y.LineOfBusiness)
                    .GroupRowsBy(y => y.AmountType)
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "TransposedGroupedByRowsAndByColumns",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x =>
                x.Transpose<double>()
                    .GroupColumnsBy(y => y.LineOfBusiness)
                    .GroupRowsBy(y => y.AmountType)
        );
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByColumnTwoDimensions",
            TwoDimValue.Data,
            b => b.GroupColumnsBy(x => x.Dim1).GroupColumnsBy(x => x.Dim2)
        );
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByColumnOneDimension",
            TwoDimValue.Data,
            b => b.GroupColumnsBy(x => x.Dim1)
        );
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByRowTwoDimensions",
            TwoDimValue.Data,
            b => b.GroupRowsBy(x => x.Dim1).GroupRowsBy(x => x.Dim2)
        );
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByRowTwoDimensionsTotalsBug",
            TwoDimValue.Data.Take(3),
            b => b.GroupRowsBy(x => x.Dim2).GroupRowsBy(x => x.Dim1)
        );
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByRowOneDimension",
            TwoDimValue.Data,
            b => b.GroupRowsBy(x => x.Dim1)
        );
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimension",
            ValueWithHierarchicalDimension.Data,
            b => b.GroupRowsBy(x => x.DimA)
        );
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimensionOptionsA1",
            ValueWithHierarchicalDimension.Data,
            b =>
                b.GroupRowsBy(x => x.DimA)
                    .WithHierarchicalDimensionOptions(o =>
                        o.LevelMin<TestHierarchicalDimensionA>(1)
                    )
        );
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimensionOptionsA2",
            ValueWithHierarchicalDimension.Data,
            b =>
                b.GroupRowsBy(x => x.DimA)
                    .WithHierarchicalDimensionOptions(o =>
                        o.LevelMin<TestHierarchicalDimensionA>(1)
                            .LevelMax<TestHierarchicalDimensionA>(1)
                    )
        );
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimensionOptionsAFlat",
            ValueWithHierarchicalDimension.Data,
            b =>
                b.GroupRowsBy(x => x.DimA)
                    .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>())
        );
        yield return new TestCase<
            ValueWithTwoHierarchicalDimensions,
            ValueWithTwoHierarchicalDimensions
        >(
            "HierarchicalDimensionTwo",
            ValueWithTwoHierarchicalDimensions.Data,
            b => b.GroupRowsBy(x => x.DimA).GroupRowsBy(x => x.DimB)
        );
    }

    private class TestCase<T, TIntermediate, TAggregate>
    {
        private readonly IEnumerable<T> data;
        private readonly Func<
            PivotBuilder<T, T, T>,
            PivotBuilder<T, TIntermediate, TAggregate>
        > pivotBuilder;
        private readonly string benchmarkFile;

        public static implicit operator object[](TestCase<T, TIntermediate, TAggregate> testCase) =>
            new object[] { testCase.benchmarkFile, testCase.data, testCase.pivotBuilder };

        public TestCase(
            string benchmarkFile,
            IEnumerable<T> data,
            Func<PivotBuilder<T, T, T>, PivotBuilder<T, TIntermediate, TAggregate>> pivotBuilder
        )
        {
            this.data = data;
            this.pivotBuilder = pivotBuilder;
            this.benchmarkFile = benchmarkFile;
        }
    }

    private class TestCase<T, TAggregate> : TestCase<T, TAggregate, TAggregate>
    {
        public TestCase(
            string benchmarkFile,
            IEnumerable<T> data,
            Func<PivotBuilder<T, T, T>, PivotBuilder<T, TAggregate, TAggregate>> pivotBuilder
        )
            : base(benchmarkFile, data, pivotBuilder) { }
    }

    private class DataCubeTestCase<TElement, TIntermediate, TAggregate>
    {
        private readonly IEnumerable<IDataCube<TElement>> data;
        private readonly Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate>
        > pivotBuilder;
        private readonly string benchmarkFile;

        public static implicit operator object[](
            DataCubeTestCase<TElement, TIntermediate, TAggregate> testCase
        ) => new object[] { testCase.benchmarkFile, testCase.data, testCase.pivotBuilder };

        public DataCubeTestCase(
            string benchmarkFile,
            IEnumerable<IDataCube<TElement>> data,
            Func<
                DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
                DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate>
            > pivotBuilder
        )
        {
            this.data = data;
            this.pivotBuilder = pivotBuilder;
            this.benchmarkFile = benchmarkFile;
        }
    }

    private class DataCubeTestCase<TElement, TAggregate>
        : DataCubeTestCase<TElement, TAggregate, TAggregate>
    {
        public DataCubeTestCase(
            string benchmarkFile,
            IEnumerable<IDataCube<TElement>> data,
            Func<
                DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
                DataCubePivotBuilder<IDataCube<TElement>, TElement, TAggregate, TAggregate>
            > pivotBuilder
        )
            : base(benchmarkFile, data, pivotBuilder) { }
    }

    private async Task ExecuteDataCubeTest<TElement>(
        string fileName,
        IEnumerable<IDataCube<TElement>> data,
        Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>
        > builder
    )
    {
        var initial = (GetHost().GetWorkspace()).ForDataCubes(data);

        var pivotBuilder = builder(initial);

        var model = await GetModel(pivotBuilder);
        await model.JsonShouldMatch(Options, $"{fileName}.json");
    }

    private async Task ExecuteDataCubeCountTest<TElement>(
        string fileName,
        IEnumerable<IDataCube<TElement>> data,
        Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, int, int>
        > builder
    )
    {
        var initial = (GetHost().GetWorkspace()).ForDataCubes(data);

        var pivotBuilder = builder(initial);

        var model = await GetModel(pivotBuilder);
        await model.JsonShouldMatch(Options, $"{fileName}.json");
    }

    private async Task ExecuteDataCubeAverageTest<TElement>(
        string fileName,
        IEnumerable<IDataCube<TElement>> data,
        Func<
            DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>,
            DataCubePivotBuilder<IDataCube<TElement>, TElement, (TElement sum, int count), TElement>
        > builder
    )
    {
        var initial = (GetHost().GetWorkspace()).ForDataCubes(data);

        var pivotBuilder = builder(initial);

        var model = await GetModel(pivotBuilder);
        await model.JsonShouldMatch(Options, $"{fileName}.json");
    }

    protected virtual async Task<PivotModel> GetModel<T, TIntermediate, TAggregate>(
        PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder
    )
    {
        return await pivotBuilder.Execute().FirstAsync();
    }

    protected virtual async Task<PivotModel> GetModel<TElement, TIntermediate, TAggregate>(
        DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate> pivotBuilder
    )
    {
        return await pivotBuilder.Execute().FirstAsync();
    }
}
