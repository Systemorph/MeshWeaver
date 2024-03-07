using FluentAssertions;
using OpenSmc.Arithmetics;
using OpenSmc.Collections;
using OpenSmc.DataCubes;
using OpenSmc.Fixture;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;
using OpenSmc.Scopes;
using OpenSmc.Scopes.DataCubes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.ServiceProvider;
using OpenSmc.TestDomain;
using OpenSmc.TestDomain.Cubes;
using OpenSmc.TestDomain.Scopes;
using OpenSmc.TestDomain.SimpleData;
using Xunit.Abstractions;

namespace OpenSmc.Pivot.Test;

public static class VerifyExtension
{
    public static async Task Verify(this object model, string fileName)
    {
        await Verifier.Verify(model)
            .UseFileName(fileName)
            .UseDirectory("Json");
    }
}

public class PivotTest : TestBase //HubTestBase
{
    [Inject] protected IScopeFactory ScopeFactory;

    public PivotTest(ITestOutputHelper output) : base(output)
    {
        Services.RegisterScopesAndArithmetics();
    }

    public override void Initialize()
    {
        base.Initialize();
        ServiceProvider.InitializeDataCubesInterceptor();
    }


    [Theory]
    [MemberData(nameof(TestCases))]
    public async Task Reports<T, TAggregate>(string fileName, IEnumerable<T> data, Func<PivotBuilder<T, T, T>, PivotBuilder<T, TAggregate, TAggregate>> builder)
    {
        var initial = PivotFactory.ForObjects(data)
            .WithQuerySource(new StaticDataFieldQuerySource());

        var pivotBuilder = builder(initial);

        var model = GetModel(pivotBuilder);
        await model.Verify(fileName);
    }

    [Theory]
    [MemberData(nameof(TestCasesCount))]
    public async Task ReportsCounts<T>(string fileName, IEnumerable<T> data, Func<PivotBuilder<T, T, T>, PivotBuilder<T, int, int>> builder)
    {
        var initial = PivotFactory.ForObjects(data)
            .WithQuerySource(new StaticDataFieldQuerySource());

        var pivotBuilder = builder(initial);

        var model = GetModel(pivotBuilder);
        await model.Verify(fileName);
    }

    [Theory]
    [MemberData(nameof(DataCubeScopeWithDimensionTestCases))]
    public async Task DataCubeScopeWithDimensionReports<TElement>(string fileName, Func<IScopeFactory, IEnumerable<IDataCube<TElement>>> dataGen, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> pivotBuilder)
    {
        var data = dataGen(ScopeFactory);
        await ExecuteDataCubeTest(fileName, data, pivotBuilder);
    }

    [Theory]
    [MemberData(nameof(DataCubeTestCases))]
    public async Task DataCubeReports<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> pivotBuilder)
    {
        await ExecuteDataCubeTest(fileName, data, pivotBuilder);
    }

    [Theory]
    [MemberData(nameof(DataCubeCountTestCases))]
    public async Task DataCubeCountReports<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, int, int>> pivotBuilder)
    {
        await ExecuteDataCubeCountTest(fileName, data, pivotBuilder);
    }

    [Theory]
    [MemberData(nameof(DataCubeAverageTestCases))]
    public async Task DataCubeAverageReports<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, (TElement sum, int count), TElement>> pivotBuilder)
    {
        await ExecuteDataCubeAverageTest(fileName, data, pivotBuilder);
    }

    [Theory]
    [MemberData(nameof(ScopeDataCubeTestCases))]
    public async Task ScopeDataCubeReports<TElement>(string fileName, Func<IScopeFactory, IEnumerable<IDataCube<TElement>>> dataGen, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> pivotBuilder)
    {
        var data = dataGen(ScopeFactory);
        await ExecuteDataCubeTest(fileName, data, pivotBuilder);
    }

    [Fact]
    public void NullQuerySourceShouldFlatten()
    {
        PivotModel qs = null;
        var exception = Record.Exception(() => qs = PivotFactory.ForDataCube(ValueWithHierarchicalDimension.Data.ToDataCube())
            .SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
            .Execute());
        Assert.Null(exception);
        Assert.NotNull(qs);

        var flattened = PivotFactory.ForDataCube(ValueWithHierarchicalDimension.Data.ToDataCube())
            .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>())
            .SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
            .Execute();

        qs.Columns.Should().BeEquivalentTo(flattened.Columns);
        qs.Rows.Should().BeEquivalentTo(flattened.Rows);
        qs.HasRowGrouping.Should().Be(flattened.HasRowGrouping);
    }

    [Fact]
    public void DataCubeScopeWithDimensionPropertiesErr()
    {
        var storage = new YearAndQuarterAndCompanyIdentityStorage((2021, 1));
        var scopes = ScopeFactory.ForIdentities(storage.Identities, storage)
            .ToScopes<IDataCubeScopeWithValueAndDimensionErr>();

        void Report() => PivotFactory.ForDataCubes(scopes).SliceColumnsBy(nameof(Country)).Execute();
        var ex = Assert.Throws<InvalidOperationException>(Report);
        ex.Message.Should().Be($"Duplicate dimensions: '{nameof(Country)}'");
    }

    [Fact]
    public void DataCubeScopeWithDimensionPropertiesErr1()
    {
        var storage = new YearAndQuarterAndCompanyIdentityStorage((2021, 1));
        var scopes = ScopeFactory.ForIdentities(storage.Identities, storage)
            .ToScopes<IDataCubeScopeWithValueAndDimensionErr1>();

        void Report() => PivotFactory.ForDataCubes(scopes).SliceColumnsBy("MyCountry").Execute();
        var ex = Assert.Throws<InvalidOperationException>(Report);//.WithMessage<InvalidOperationException>("Duplicate dimensions: 'MyCountry'");
        ex.Message.Should().Be("Duplicate dimensions: 'MyCountry'");
    }

    public static IEnumerable<object[]> DataCubeCountTestCases()
    {
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b
                .WithAggregation(a => a.Count())
                .SliceRowsBy(nameof(Currency))
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount1",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b
                .WithAggregation(a => a.Count())
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount2",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b
                .WithAggregation(a => a.Count())
                .SliceRowsBy(nameof(Currency))
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount3",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b
                .WithAggregation(a => a.Count())
                .SliceRowsBy(nameof(Country))
                .SliceRowsBy(nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount4",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b
                .WithAggregation(a => a.Count())
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceRowsBy(nameof(Country))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, int>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionCount",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b
                .WithAggregation(a => a.Count())
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
    }


    public static IEnumerable<object[]> DataCubeAverageTestCases()
    {
        yield return new DataCubeTestCase<CashflowElement, (CashflowElement sum, int count), CashflowElement>("DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyAverage",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b.WithAggregation(a => a.Average((s, c) => ArithmeticOperations.Divide(s, c)))
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, (CashflowElement sum, int count), CashflowElement>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionAverage",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.WithAggregation(a => a.Average((s, c) => ArithmeticOperations.Divide(s, c)))
                .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
    }

    public static IEnumerable<object[]> DataCubeTestCases()
    {
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>("DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTwiceByRowOnceByColumnOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceRowsByAggregateByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b);
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceRowsByAggregateByDimension2",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Currency)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceColumnsByAggregateByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Currency)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceColumnsByAggregateByDimension2",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country))
                .SliceColumnsBy(nameof(Currency)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSliceColumnsByAggregateByDimension3",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Currency))
                .SliceRowsBy(nameof(Country)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTriceByRowOneDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness), nameof(Split)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedTriceByRowOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness), nameof(Split)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedOnceByRowOnceByColumnOneDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country))
                .SliceColumnsBy(nameof(LineOfBusiness)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedOnceByRowOnceByColumnOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Country))
                .SliceColumnsBy(nameof(LineOfBusiness)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnOneDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnOneDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnThreeDimensions",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Country), nameof(LineOfBusiness), nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeSlicedByColumnThreeDimensions2",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(Country), nameof(LineOfBusiness))
                .SliceColumnsBy(nameof(AmountType)));
        yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
            "DataCubeTransposed",
            CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
            b => b.Transpose<double>());
        yield return new DataCubeTestCase<TwoDimValue, TwoDimValue>(
            "NullGroupSliceByRowTwoDimensionsTotalsBug",
            TwoDimValue.Data.Take(3).ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Dim2), nameof(Dim1)));
        yield return new DataCubeTestCase<TwoDimValue, TwoDimValue>(
            "NullGroupSliceByRowSliceByColumn",
            TwoDimValue.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(Dim1))
                .SliceColumnsBy(nameof(Dim2)));
        yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
        ("HierarchicalDimension2",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA)));
        yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
        ("HierarchicalDimensionOptionsA12",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)));
        yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
        ("HierarchicalDimensionOptionsA22",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)
                    .LevelMax<TestHierarchicalDimensionA>(1)));
        yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
        ("HierarchicalDimensionOptionsAFlat2",
            ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>()));
        yield return new DataCubeTestCase<ValueWithTwoAggregateByHierarchicalDimensions, ValueWithTwoAggregateByHierarchicalDimensions>
        ("HierarchicalDimensionTwoAggregateBy",
            ValueWithTwoAggregateByHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b);
        yield return new DataCubeTestCase<ValueWithTwoAggregateByHierarchicalDimensions, ValueWithTwoAggregateByHierarchicalDimensions>
        ("HierarchicalDimensionTwoAggregateBySliceByColumn",
            ValueWithTwoAggregateByHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(ValueWithHierarchicalDimension.DimA)));
        yield return new DataCubeTestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>
        ("HierarchicalDimensionTwo2",
            ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA), nameof(ValueWithTwoHierarchicalDimensions.DimB)));
        yield return new DataCubeTestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>
        ("HierarchicalDimensionTwoByColumns",
            ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(ValueWithHierarchicalDimension.DimA), nameof(ValueWithTwoHierarchicalDimensions.DimB)));
        yield return new DataCubeTestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>
        ("HierarchicalDimensionSliceColumnsByB",
            ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceColumnsBy(nameof(ValueWithTwoHierarchicalDimensions.DimB)));
        yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>
        ("HierarchicalDimensionMix1",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimA))
                .SliceColumnsBy(nameof(ValueWithMixedDimensions.DimD)));
        yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>
        ("HierarchicalDimensionMix2",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimA), nameof(ValueWithMixedDimensions.DimD)));
        yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>
        ("HierarchicalDimensionMix3",
            ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
            b => b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimD), nameof(ValueWithMixedDimensions.DimA)));
    }

    public static IEnumerable<object[]> DataCubeScopeWithDimensionTestCases()
    {
        var storage = new YearAndQuarterAndCompanyIdentityStorage((2021, 1), (2020, 3));

        yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScopeWithValueAndDimension>, IDataCubeScopeWithValueAndDimension, IDataCubeScopeWithValueAndDimension>("DataCubeScopeWithDimension",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension>(),
            b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension.Country), nameof(Company)));
        yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>("DataCubeScopeWithDimension2",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension2>(),
            b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension2.ScopeCountry), nameof(Company)));
        yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>("DataCubeScopeWithDimension3",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension3>(),
            b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension3.ScopeCountry), nameof(Company)));
        yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>("DataCubeScopeWithDimension4",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension4>(),
            b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension4.ScopeCountry), nameof(Company)));
    }

    public static IEnumerable<object[]> ScopeDataCubeTestCases()
    {
        var storage = new YearAndQuarterAndCompanyIdentityStorage((2021, 1));

        // TODO: uncomment this #20722  (2021-08-19, Andrei Sirotenko)
        // yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
        //                                                                                                      "ScopeWithDataCubePropertiesSlicedColumns",
        //                                                                                                      sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithDataCubeProperties>().Aggregate().RepeatOnce(),
        //                                                                                                      b => b.SliceColumnsBy(nameof(AmountType)));
        yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScope>, IDataCubeScope, IDataCubeScope>(
            "DataCubeScope",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScope>(),
            b => b.SliceColumnsBy(nameof(AmountType)));
        yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
            "ScopeWithElementPropertiesSlicedColumns",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithElementProperties>(),
            b => b.SliceColumnsBy(nameof(AmountType)));
        yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
            "ScopeWithElementPropertiesSlicedRows",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithElementProperties>(),
            b => b.SliceColumnsBy(nameof(Currency))
                .SliceRowsBy(nameof(AmountType), "Year"));
        yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScope>, IDataCubeScope, IDataCubeScope>(
            "DataCubeScopeTransposedSliceByRow",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScope>(),
            b => b.Transpose<double>()
                .SliceRowsBy("Year"));
        yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScope>, IDataCubeScope, IDataCubeScope>(
            "DataCubeScopeTransposed",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScope>(),
            b => b.Transpose<double>());
        yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
            "ScopeWithDataCubePropertiesSlicedColumns",
            sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithDataCubeProperties>(),
            b => b.SliceColumnsBy(nameof(AmountType)));
    }

    public static IEnumerable<object[]> TestCasesCount()
    {
        yield return new TestCase<SimpleAccounting, int>("AutorenderedBasicCount",
            SimpleAccountingFactory.GetData(3),
            x => x
                .WithAggregation(a => a.Count()));


        yield return new TestCase<CashflowElement, int>("GroupedByRowsAndByColumnsCount",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.WithAggregation(a => a.Count())
                .GroupColumnsBy(y => y.LineOfBusiness)
                .GroupRowsBy(y => y.AmountType)
        );
    }

    public static IEnumerable<object[]> TestCases()
    {
        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "AutorenderedBasic",
            SimpleAccountingFactory.GetData(3),
            x => x);

        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "AutorenderedTransposedBasic",
            SimpleAccountingFactory.GetData(1),
            x => x.Transpose<double>());


        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "AutorenderedTransposedMultipleBasic",
            SimpleAccountingFactory.GetData(3),
            x => x.Transpose<double>());


        yield return new TestCase<SimpleAccounting, SimpleAccounting>(
            "BasicWithCustomRowDefinitions",
            SimpleAccountingFactory.GetData(3),
            rb =>
                rb.GroupRowsBy(o => new RowGroup(o.SystemName, o.DisplayName, PivotConst.AutomaticEnumerationPivotGrouperName)));


        yield return new TestCase<SimpleAccountingNamed, SimpleAccountingNamed>(
            "BasicWithCustomRowDefinitions2",
            SimpleAccountingFactory.GetData<SimpleAccountingNamed>(3),
            rb => rb);

        yield return new TestCase<SimpleAccountingNamed, SimpleAccountingNamed>(
            "TransposedWithCustomGroupDefinitions",
            SimpleAccountingFactory.GetData<SimpleAccountingNamed>(3),
            rb =>
                rb.Transpose<double>());

        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupRowsByDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType))));
        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupRowsByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType))));

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupRowsByDimensionPropertySingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupRowsBy(y => y.AmountType));

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupRowsByDimensionProperty",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupRowsBy(y => y.AmountType));

        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupColumnsByDimensionSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "ManualGroupColumnsByDimension",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupColumnsByDimensionPropertySingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x
                .Transpose<double>()
                .GroupColumnsBy(y => y.AmountType)
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupColumnsByDimensionProperty",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x
                .Transpose<double>()
                .GroupColumnsBy(y => y.AmountType)
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCubeSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x.GroupRowsBy(y => new RowGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                .GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCube",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x.GroupRowsBy(y => new RowGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                .GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCubeTransposedSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x
                .Transpose<double>()
                .GroupColumnsBy(y => new ColumnGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                .GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TwoGroupsDataCubeTransposed",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x
                .Transpose<double>()
                .GroupColumnsBy(y => new ColumnGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                .GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupedByRowsAndByColumnsSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x
                .GroupColumnsBy(y => y.LineOfBusiness)
                .GroupRowsBy(y => y.AmountType)
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "GroupedByRowsAndByColumns",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x
                .GroupColumnsBy(y => y.LineOfBusiness)
                .GroupRowsBy(y => y.AmountType)
        );

        yield return new TestCase<CashflowElement, CashflowElement>(
            "TransposedGroupedByRowsAndByColumnsSingleCurrency",
            CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
            x => x
                .Transpose<double>()
                .GroupColumnsBy(y => y.LineOfBusiness)
                .GroupRowsBy(y => y.AmountType)
        );
        yield return new TestCase<CashflowElement, CashflowElement>(
            "TransposedGroupedByRowsAndByColumns",
            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
            x => x
                .Transpose<double>()
                .GroupColumnsBy(y => y.LineOfBusiness)
                .GroupRowsBy(y => y.AmountType)
        );
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByColumnTwoDimensions",
            TwoDimValue.Data,
            b => b.GroupColumnsBy(x => x.Dim1)
                .GroupColumnsBy(x => x.Dim2));
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByColumnOneDimension",
            TwoDimValue.Data,
            b => b.GroupColumnsBy(x => x.Dim1));
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByRowTwoDimensions",
            TwoDimValue.Data,
            b => b.GroupRowsBy(x => x.Dim1)
                .GroupRowsBy(x => x.Dim2));
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByRowTwoDimensionsTotalsBug",
            TwoDimValue.Data.Take(3),
            b => b.GroupRowsBy(x => x.Dim2)
                .GroupRowsBy(x => x.Dim1));
        yield return new TestCase<TwoDimValue, TwoDimValue>(
            "NullGroupGroupByRowOneDimension",
            TwoDimValue.Data,
            b => b.GroupRowsBy(x => x.Dim1));
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimension",
            ValueWithHierarchicalDimension.Data,
            b => b.GroupRowsBy(x => x.DimA));
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimensionOptionsA1",
            ValueWithHierarchicalDimension.Data,
            b => b.GroupRowsBy(x => x.DimA)
                .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)));
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimensionOptionsA2",
            ValueWithHierarchicalDimension.Data,
            b => b.GroupRowsBy(x => x.DimA)
                .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)
                    .LevelMax<TestHierarchicalDimensionA>(1)));
        yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
            "HierarchicalDimensionOptionsAFlat",
            ValueWithHierarchicalDimension.Data,
            b => b.GroupRowsBy(x => x.DimA)
                .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>()));
        yield return new TestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>(
            "HierarchicalDimensionTwo",
            ValueWithTwoHierarchicalDimensions.Data,
            b => b.GroupRowsBy(x => x.DimA)
                .GroupRowsBy(x => x.DimB));
    }


    private class TestCase<T, TIntermediate, TAggregate>
    {
        private readonly IEnumerable<T> data;
        private readonly Func<PivotBuilder<T, T, T>, PivotBuilder<T, TIntermediate, TAggregate>> pivotBuilder;
        private readonly string benchmarkFile;

        public static implicit operator object[](TestCase<T, TIntermediate, TAggregate> testCase) => new object[] { testCase.benchmarkFile, testCase.data, testCase.pivotBuilder };

        public TestCase(string benchmarkFile, IEnumerable<T> data, Func<PivotBuilder<T, T, T>, PivotBuilder<T, TIntermediate, TAggregate>> pivotBuilder)
        {
            this.data = data;
            this.pivotBuilder = pivotBuilder;
            this.benchmarkFile = benchmarkFile;
        }
    }

    private class TestCase<T, TAggregate> : TestCase<T, TAggregate, TAggregate>
    {
        public TestCase(string benchmarkFile, IEnumerable<T> data, Func<PivotBuilder<T, T, T>, PivotBuilder<T, TAggregate, TAggregate>> pivotBuilder)
            : base(benchmarkFile, data, pivotBuilder)
        {
        }
    }

    private class DataCubeTestCase<TElement, TIntermediate, TAggregate>
    {
        private readonly IEnumerable<IDataCube<TElement>> data;
        private readonly Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate>> pivotBuilder;
        private readonly string benchmarkFile;

        public static implicit operator object[](DataCubeTestCase<TElement, TIntermediate, TAggregate> testCase) => new object[] { testCase.benchmarkFile, testCase.data, testCase.pivotBuilder };

        public DataCubeTestCase(string benchmarkFile, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate>> pivotBuilder)
        {
            this.data = data;
            this.pivotBuilder = pivotBuilder;
            this.benchmarkFile = benchmarkFile;
        }
    }

    private class DataCubeTestCase<TElement, TAggregate> : DataCubeTestCase<TElement, TAggregate, TAggregate>
    {
        public DataCubeTestCase(string benchmarkFile, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TAggregate, TAggregate>> pivotBuilder)
            : base(benchmarkFile, data, pivotBuilder)
        {
        }
    }

    private class ScopeDataCubeTestCase<TScope, TElement, TAggregate>
        where TScope : IDataCube<TElement>
    {
        private readonly Func<IScopeFactory, IEnumerable<TScope>> dataGenerator;
        private readonly Func<DataCubePivotBuilder<TScope, TElement, TElement, TElement>, DataCubePivotBuilder<TScope, TElement, TAggregate, TAggregate>> pivotBuilder;
        private readonly string benchmarkFile;

        public static implicit operator object[](ScopeDataCubeTestCase<TScope, TElement, TAggregate> testCase) => new object[] { testCase.benchmarkFile, testCase.dataGenerator, testCase.pivotBuilder };

        public ScopeDataCubeTestCase(string benchmarkFile, Func<IScopeFactory, IEnumerable<TScope>> dataGenerator, Func<DataCubePivotBuilder<TScope, TElement, TElement, TElement>, DataCubePivotBuilder<TScope, TElement, TAggregate, TAggregate>> pivotBuilder)
        {
            this.dataGenerator = dataGenerator;
            this.pivotBuilder = pivotBuilder;
            this.benchmarkFile = benchmarkFile;
        }
    }

    private async Task ExecuteDataCubeTest<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> builder)
    {
        var initial = PivotFactory
            .ForDataCubes(data)
            .WithQuerySource(new StaticDataFieldQuerySource());

        var pivotBuilder = builder(initial);

        var model = GetModel(pivotBuilder);
        await model.Verify(fileName);
    }

    private async Task ExecuteDataCubeCountTest<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, int, int>> builder)
    {
        var initial = PivotFactory
            .ForDataCubes(data)
            .WithQuerySource(new StaticDataFieldQuerySource());

        var pivotBuilder = builder(initial);

        var model = GetModel(pivotBuilder);
        await model.Verify(fileName);
    }

    private async Task ExecuteDataCubeAverageTest<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, (TElement sum, int count), TElement>> builder)
    {
        var initial = PivotFactory
            .ForDataCubes(data)
            .WithQuerySource(new StaticDataFieldQuerySource());

        var pivotBuilder = builder(initial);

        var model = GetModel(pivotBuilder);
        await model.Verify(fileName);
    }

    protected virtual object GetModel<T, TIntermediate, TAggregate>(PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder)
    {
        return pivotBuilder.Execute();
    }

    protected virtual object GetModel<TElement, TIntermediate, TAggregate>(DataCubePivotBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate> pivotBuilder)
    {
        return pivotBuilder.Execute();
    }


}