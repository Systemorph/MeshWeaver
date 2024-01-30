using Autofac.Core;
using FluentAssertions;
using FluentAssertions.Common;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics;
using OpenSmc.Collections;
using OpenSmc.DataCubes;
using OpenSmc.Fixture;
using OpenSmc.Json.Assertions;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;
using OpenSmc.TestDomain;
using OpenSmc.TestDomain.Cubes;
using OpenSmc.TestDomain.Scopes;
using OpenSmc.TestDomain.SimpleData;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Pivot.Test
{
    public class PivotTest : TestBase
    {
        [Inject] protected IScopeFactory ScopeFactory;
        [Inject] protected ISerializationService SerializationService;

        public PivotTest(ITestOutputHelper toh)
            : base(toh)
        {
            Modules.Add<DataCubes.ModuleSetup>();
            Services.RegisterScopes();
            Services.AddSingleton<IEventsRegistry>(_ => new EventsRegistry(null));
            Services.AddSingleton<ISerializationService, SerializationService>();
            var registry = new CustomSerializationRegistry();
            Services.AddSingleton(registry);
            Services.AddSingleton<ICustomSerializationRegistry>(registry);
        }

        [Theory]
        [MemberData(nameof(TestCases))]
        public void Reports<T, TAggregate>(string fileName, IEnumerable<T> data, Func<PivotBuilder<T, T, T>, PivotBuilder<T, TAggregate, TAggregate>> builder)
        {
            var initial = PivotFactory.ForObjects(data)
                                      .WithQuerySource(new StaticDataFieldQuerySource());

            var pivotBuilder = builder(initial);

            var model = GetModel(pivotBuilder);
            model.JsonShouldMatch(SerializationService, fileName);
        }

        [Theory]
        [MemberData(nameof(TestCasesCount))]
        public void ReportsCounts<T>(string fileName, IEnumerable<T> data, Func<PivotBuilder<T, T, T>, PivotBuilder<T, int, int>> builder)
        {
            var initial = PivotFactory.ForObjects(data)
                                      .WithQuerySource(new StaticDataFieldQuerySource());

            var pivotBuilder = builder(initial);

            var model = GetModel(pivotBuilder);
            model.JsonShouldMatch(SerializationService, fileName);
        }

        [Theory]
        [MemberData(nameof(DataCubeScopeWithDimensionTestCases))]
        public void DataCubeScopeWithDimensionReports<TElement>(string fileName, Func<IScopeFactory, IEnumerable<IDataCube<TElement>>> dataGen, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> pivotBuilder)
        {
            var data = dataGen(ScopeFactory);
            ExecuteDataCubeTest(fileName, data, pivotBuilder);
        }

        [Theory]
        [MemberData(nameof(DataCubeTestCases))]
        public void DataCubeReports<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> pivotBuilder)
        {
             ExecuteDataCubeTest(fileName, data, pivotBuilder);
        }

        [Theory]
        [MemberData(nameof(DataCubeCountTestCases))]
        public void DataCubeCountReports<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, int, int>> pivotBuilder)
        {
            ExecuteDataCubeCountTest(fileName, data, pivotBuilder);
        }

        [Theory]
        [MemberData(nameof(DataCubeAverageTestCases))]
        public void DataCubeAverageReports<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, (TElement sum, int count), TElement>> pivotBuilder)
        {
            ExecuteDataCubeAverageTest(fileName, data, pivotBuilder);
        }

        [Theory]
        [MemberData(nameof(ScopeDataCubeTestCases))]
        public void ScopeDataCubeReports<TElement>(string fileName, Func<IScopeFactory, IEnumerable<IDataCube<TElement>>> dataGen, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> pivotBuilder)
        {
            var data = dataGen(ScopeFactory);
            ExecuteDataCubeTest(fileName, data, pivotBuilder);
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
                                                                    "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount.json",
                                                                    CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                    b => b
                                                                         .WithAggregation(a => a.Count())
                                                                         .SliceRowsBy(nameof(Currency))
                                                                         .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                         .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, int>(
                                                                    "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount.json",
                                                                    CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                    b => b
                                                                         .WithAggregation(a => a.Count())
                                                                         .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                         .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, int>(
                                                                    "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount.json",
                                                                    CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                    b => b
                                                                         .WithAggregation(a => a.Count())
                                                                         .SliceRowsBy(nameof(Currency))
                                                                         .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                         .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, int>(
                                                                    "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount.json",
                                                                    CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                    b => b
                                                                         .WithAggregation(a => a.Count())
                                                                         .SliceRowsBy(nameof(Country))
                                                                         .SliceRowsBy(nameof(LineOfBusiness))
                                                                         .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, int>(
                                                                    "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyCount.json",
                                                                    CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                    b => b
                                                                         .WithAggregation(a => a.Count())
                                                                         .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                         .SliceRowsBy(nameof(Country))
                                                                         .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, int>(
                                                                    "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionCount.json",
                                                                    CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                    b => b
                                                                         .WithAggregation(a => a.Count())
                                                                         .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                         .SliceColumnsBy(nameof(AmountType)));
        }


        public static IEnumerable<object[]> DataCubeAverageTestCases()
        {
            yield return new DataCubeTestCase<CashflowElement, (CashflowElement sum, int count), CashflowElement>("DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrencyAverage.json",
                                                                                                                  CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                                                                  b => b.WithAggregation(a => a.Average((s, c) => ArithmeticOperations.Divide(s, c)))
                                                                                                                        .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                                                                        .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, (CashflowElement sum, int count), CashflowElement>(
                                                                                                                  "DataCubeSlicedTwiceByRowOnceByColumnOneDimensionAverage.json",
                                                                                                                  CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                                                  b => b.WithAggregation(a => a.Average((s, c) => ArithmeticOperations.Divide(s, c)))
                                                                                                                        .SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                                                                        .SliceColumnsBy(nameof(AmountType)));
        }

        public static IEnumerable<object[]> DataCubeTestCases()
        {
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>("DataCubeSlicedTwiceByRowOnceByColumnOneDimensionSingleCurrency.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                                      .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedTwiceByRowOnceByColumnOneDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness))
                                                                                      .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSliceRowsByAggregateByDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b);
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSliceRowsByAggregateByDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Currency)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSliceColumnsByAggregateByDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceColumnsBy(nameof(Currency)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSliceColumnsByAggregateByDimension2.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country))
                                                                                      .SliceColumnsBy(nameof(Currency)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSliceColumnsByAggregateByDimension2.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceColumnsBy(nameof(Currency))
                                                                                      .SliceRowsBy(nameof(Country)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedTriceByRowOneDimensionSingleCurrency.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness), nameof(Split)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedTriceByRowOneDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country), nameof(LineOfBusiness), nameof(Split)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedOnceByRowOnceByColumnOneDimensionSingleCurrency.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country))
                                                                                      .SliceColumnsBy(nameof(LineOfBusiness)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedOnceByRowOnceByColumnOneDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceRowsBy(nameof(Country))
                                                                                      .SliceColumnsBy(nameof(LineOfBusiness)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedByColumnOneDimensionSingleCurrency.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedByColumnOneDimension.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedByColumnThreeDimensions.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceColumnsBy(nameof(Country), nameof(LineOfBusiness), nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeSlicedByColumnThreeDimensions.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.SliceColumnsBy(nameof(Country), nameof(LineOfBusiness))
                                                                                      .SliceColumnsBy(nameof(AmountType)));
            yield return new DataCubeTestCase<CashflowElement, CashflowElement>(
                                                                                "DataCubeTransposed.json",
                                                                                CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube().RepeatOnce(),
                                                                                b => b.Transpose<double>());
            yield return new DataCubeTestCase<TwoDimValue, TwoDimValue>(
                                                                        "NullGroupSliceByRowTwoDimensionsTotalsBug.json",
                                                                        TwoDimValue.Data.Take(3).ToDataCube().RepeatOnce(),
                                                                        b => b.SliceRowsBy(nameof(Dim2), nameof(Dim1)));
            yield return new DataCubeTestCase<TwoDimValue, TwoDimValue>(
                                                                        "NullGroupSliceByRowSliceByColumn.json",
                                                                        TwoDimValue.Data.ToDataCube().RepeatOnce(),
                                                                        b => b.SliceRowsBy(nameof(Dim1))
                                                                              .SliceColumnsBy(nameof(Dim2)));
            yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
                ("HierarchicalDimension.json",
                 ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA)));
            yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
                ("HierarchicalDimensionOptionsA1.json",
                 ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                       .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)));
            yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
                ("HierarchicalDimensionOptionsA2.json",
                 ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                       .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)
                                                               .LevelMax<TestHierarchicalDimensionA>(1)));
            yield return new DataCubeTestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>
                ("HierarchicalDimensionOptionsAFlat.json",
                 ValueWithHierarchicalDimension.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA))
                       .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>()));
            yield return new DataCubeTestCase<ValueWithTwoAggregateByHierarchicalDimensions, ValueWithTwoAggregateByHierarchicalDimensions>
                ("HierarchicalDimensionTwoAggregateBy.json",
                 ValueWithTwoAggregateByHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b);
            yield return new DataCubeTestCase<ValueWithTwoAggregateByHierarchicalDimensions, ValueWithTwoAggregateByHierarchicalDimensions>
                ("HierarchicalDimensionTwoAggregateBySliceByColumn.json",
                 ValueWithTwoAggregateByHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceColumnsBy(nameof(ValueWithHierarchicalDimension.DimA)));
            yield return new DataCubeTestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>
                ("HierarchicalDimensionTwo.json",
                 ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithHierarchicalDimension.DimA), nameof(ValueWithTwoHierarchicalDimensions.DimB)));
            yield return new DataCubeTestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>
                ("HierarchicalDimensionTwoByColumns.json",
                 ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceColumnsBy(nameof(ValueWithHierarchicalDimension.DimA), nameof(ValueWithTwoHierarchicalDimensions.DimB)));
            yield return new DataCubeTestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>
                ("HierarchicalDimensionSliceColumnsByB.json",
                 ValueWithTwoHierarchicalDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceColumnsBy(nameof(ValueWithTwoHierarchicalDimensions.DimB)));
            yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>
                ("HierarchicalDimensionMix1.json",
                 ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimA))
                       .SliceColumnsBy(nameof(ValueWithMixedDimensions.DimD)));
            yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>
                ("HierarchicalDimensionMix2.json",
                 ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimA), nameof(ValueWithMixedDimensions.DimD)));
            yield return new DataCubeTestCase<ValueWithMixedDimensions, ValueWithMixedDimensions>
                ("HierarchicalDimensionMix3.json",
                 ValueWithMixedDimensions.Data.ToDataCube().RepeatOnce(),
                 b => b.SliceRowsBy(nameof(ValueWithMixedDimensions.DimD), nameof(ValueWithMixedDimensions.DimA)));
        }

        public static IEnumerable<object[]> DataCubeScopeWithDimensionTestCases()
        {
            var storage = new YearAndQuarterAndCompanyIdentityStorage((2021, 1), (2020, 3));

            yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScopeWithValueAndDimension>, IDataCubeScopeWithValueAndDimension, IDataCubeScopeWithValueAndDimension>("DataCubeScopeWithDimension.json",
                                                                                                                                                                             sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension>(),
                                                                                                                                                                             b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension.Country), nameof(Company)));
            yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>("DataCubeScopeWithDimension2.json",
                                                                                                                 sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension2>(),
                                                                                                                 b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension2.ScopeCountry), nameof(Company)));
            yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>("DataCubeScopeWithDimension3.json",
                                                                                                                 sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension3>(),
                                                                                                                 b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension3.ScopeCountry), nameof(Company)));
            yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>("DataCubeScopeWithDimension4.json",
                                                                                                                 sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScopeWithValueAndDimension4>(),
                                                                                                                 b => b.SliceColumnsBy(nameof(IDataCubeScopeWithValueAndDimension4.ScopeCountry), nameof(Company)));
        }

        public static IEnumerable<object[]> ScopeDataCubeTestCases()
        {
            var storage = new YearAndQuarterAndCompanyIdentityStorage((2021, 1));

            // TODO: uncomment this #20722  (2021-08-19, Andrei Sirotenko)
            // yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
            //                                                                                                      "ScopeWithDataCubePropertiesSlicedColumns.json",
            //                                                                                                      sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithDataCubeProperties>().Aggregate().RepeatOnce(),
            //                                                                                                      b => b.SliceColumnsBy(nameof(AmountType)));
            yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScope>, IDataCubeScope, IDataCubeScope>(
                                                                                                              "DataCubeScope.json",
                                                                                                              sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScope>(),
                                                                                                              b => b.SliceColumnsBy(nameof(AmountType)));
            yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
                                                                                                                 "ScopeWithElementPropertiesSlicedColumns.json",
                                                                                                                 sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithElementProperties>(),
                                                                                                                 b => b.SliceColumnsBy(nameof(AmountType)));
            yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
                                                                                                                 "ScopeWithElementPropertiesSlicedRows.json",
                                                                                                                 sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithElementProperties>(),
                                                                                                                 b => b.SliceColumnsBy(nameof(Currency))
                                                                                                                       .SliceRowsBy(nameof(AmountType), "Year"));
            yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScope>, IDataCubeScope, IDataCubeScope>(
                                                                                                              "DataCubeScopeTransposedSliceByRow.json",
                                                                                                              sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScope>(),
                                                                                                              b => b.Transpose<double>()
                                                                                                                    .SliceRowsBy("Year"));
            yield return new ScopeDataCubeTestCase<IDataCube<IDataCubeScope>, IDataCubeScope, IDataCubeScope>(
                                                                                                              "DataCubeScopeTransposed.json",
                                                                                                              sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IDataCubeScope>(),
                                                                                                              b => b.Transpose<double>());
            yield return new ScopeDataCubeTestCase<IDataCube<CashflowElement>, CashflowElement, CashflowElement>(
                                                                                                                 "ScopeWithDataCubePropertiesSlicedColumns.json",
                                                                                                                 sf => sf.ForIdentities(storage.Identities, storage).ToScopes<IScopeWithDataCubeProperties>(),
                                                                                                                 b => b.SliceColumnsBy(nameof(AmountType)));
        }

        public static IEnumerable<object[]> TestCasesCount()
        {
            yield return new TestCase<SimpleAccounting, int>("AutorenderedBasicCount.json",
                                                             SimpleAccountingFactory.GetData(3),
                                                             x => x
                                                                 .WithAggregation(a => a.Count()));


            yield return new TestCase<CashflowElement, int>("GroupedByRowsAndByColumnsCount.json",
                                                            CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                            x => x.WithAggregation(a => a.Count())
                                                                  .GroupColumnsBy(y => y.LineOfBusiness)
                                                                  .GroupRowsBy(y => y.AmountType)
                                                           );
        }

        public static IEnumerable<object[]> TestCases()
        {
            yield return new TestCase<SimpleAccounting, SimpleAccounting>(
                                                                          "AutorenderedBasic.json",
                                                                          SimpleAccountingFactory.GetData(3),
                                                                          x => x);

            yield return new TestCase<SimpleAccounting, SimpleAccounting>(
                                                                          "AutorenderedTransposedBasic.json",
                                                                          SimpleAccountingFactory.GetData(1),
                                                                          x => x.Transpose<double>());


            yield return new TestCase<SimpleAccounting, SimpleAccounting>(
                                                                          "AutorenderedTransposedMultipleBasic.json",
                                                                          SimpleAccountingFactory.GetData(3),
                                                                          x => x.Transpose<double>());


            yield return new TestCase<SimpleAccounting, SimpleAccounting>(
                                                                          "BasicWithCustomRowDefinitions.json",
                                                                          SimpleAccountingFactory.GetData(3),
                                                                          rb =>
                                                                              rb.GroupRowsBy(o => new RowGroup(o.SystemName, o.DisplayName, PivotConst.AutomaticEnumerationPivotGrouperName)));


            yield return new TestCase<SimpleAccountingNamed, SimpleAccountingNamed>(
                                                                                    "BasicWithCustomRowDefinitions.json",
                                                                                    SimpleAccountingFactory.GetData<SimpleAccountingNamed>(3),
                                                                                    rb => rb);

            yield return new TestCase<SimpleAccountingNamed, SimpleAccountingNamed>(
                                                                                    "TransposedWithCustomGroupDefinitions.json",
                                                                                    SimpleAccountingFactory.GetData<SimpleAccountingNamed>(3),
                                                                                    rb =>
                                                                                        rb.Transpose<double>());

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "ManualGroupRowsByDimensionSingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x.GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType))));
            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "ManualGroupRowsByDimension.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x.GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType))));

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "GroupRowsByDimensionPropertySingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x.GroupRowsBy(y => y.AmountType));

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "GroupRowsByDimensionProperty.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x.GroupRowsBy(y => y.AmountType));

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "ManualGroupColumnsByDimensionSingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x.GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
                                                                       );
            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "ManualGroupColumnsByDimension.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x.GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
                                                                       );

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "GroupColumnsByDimensionPropertySingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x
                                                                             .Transpose<double>()
                                                                             .GroupColumnsBy(y => y.AmountType)
                                                                       );
            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "GroupColumnsByDimensionProperty.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x
                                                                             .Transpose<double>()
                                                                             .GroupColumnsBy(y => y.AmountType)
                                                                       );

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "TwoGroupsDataCubeSingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x.GroupRowsBy(y => new RowGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                                                                              .GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
                                                                       );
            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "TwoGroupsDataCube.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x.GroupRowsBy(y => new RowGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                                                                              .GroupRowsBy(y => new RowGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
                                                                       );

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "TwoGroupsDataCubeTransposedSingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x
                                                                             .Transpose<double>()
                                                                             .GroupColumnsBy(y => new ColumnGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                                                                             .GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
                                                                       );

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "TwoGroupsDataCubeTransposed.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x
                                                                             .Transpose<double>()
                                                                             .GroupColumnsBy(y => new ColumnGroup(y.LineOfBusiness, y.LineOfBusiness, nameof(y.LineOfBusiness)))
                                                                             .GroupColumnsBy(y => new ColumnGroup(y.AmountType, y.AmountType, nameof(y.AmountType)))
                                                                       );

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "GroupedByRowsAndByColumnsSingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x
                                                                             .GroupColumnsBy(y => y.LineOfBusiness)
                                                                             .GroupRowsBy(y => y.AmountType)
                                                                       );
            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "GroupedByRowsAndByColumns.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x
                                                                             .GroupColumnsBy(y => y.LineOfBusiness)
                                                                             .GroupRowsBy(y => y.AmountType)
                                                                       );

            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "TransposedGroupedByRowsAndByColumnsSingleCurrency.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                                                                        x => x
                                                                             .Transpose<double>()
                                                                             .GroupColumnsBy(y => y.LineOfBusiness)
                                                                             .GroupRowsBy(y => y.AmountType)
                                                                       );
            yield return new TestCase<CashflowElement, CashflowElement>(
                                                                        "TransposedGroupedByRowsAndByColumns.json",
                                                                        CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                                                                        x => x
                                                                             .Transpose<double>()
                                                                             .GroupColumnsBy(y => y.LineOfBusiness)
                                                                             .GroupRowsBy(y => y.AmountType)
                                                                       );
            yield return new TestCase<TwoDimValue, TwoDimValue>(
                                                                "NullGroupGroupByColumnTwoDimensions.json",
                                                                TwoDimValue.Data,
                                                                b => b.GroupColumnsBy(x => x.Dim1)
                                                                      .GroupColumnsBy(x => x.Dim2));
            yield return new TestCase<TwoDimValue, TwoDimValue>(
                                                                "NullGroupGroupByColumnOneDimension.json",
                                                                TwoDimValue.Data,
                                                                b => b.GroupColumnsBy(x => x.Dim1));
            yield return new TestCase<TwoDimValue, TwoDimValue>(
                                                                "NullGroupGroupByRowTwoDimensions.json",
                                                                TwoDimValue.Data,
                                                                b => b.GroupRowsBy(x => x.Dim1)
                                                                      .GroupRowsBy(x => x.Dim2));
            yield return new TestCase<TwoDimValue, TwoDimValue>(
                                                                "NullGroupGroupByRowTwoDimensionsTotalsBug.json",
                                                                TwoDimValue.Data.Take(3),
                                                                b => b.GroupRowsBy(x => x.Dim2)
                                                                      .GroupRowsBy(x => x.Dim1));
            yield return new TestCase<TwoDimValue, TwoDimValue>(
                                                                "NullGroupGroupByRowOneDimension.json",
                                                                TwoDimValue.Data,
                                                                b => b.GroupRowsBy(x => x.Dim1));
            yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
                                                                                                      "HierarchicalDimension.json",
                                                                                                      ValueWithHierarchicalDimension.Data,
                                                                                                      b => b.GroupRowsBy(x => x.DimA));
            yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
                                                                                                      "HierarchicalDimensionOptionsA1.json",
                                                                                                      ValueWithHierarchicalDimension.Data,
                                                                                                      b => b.GroupRowsBy(x => x.DimA)
                                                                                                            .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)));
            yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
                                                                                                      "HierarchicalDimensionOptionsA2.json",
                                                                                                      ValueWithHierarchicalDimension.Data,
                                                                                                      b => b.GroupRowsBy(x => x.DimA)
                                                                                                            .WithHierarchicalDimensionOptions(o => o.LevelMin<TestHierarchicalDimensionA>(1)
                                                                                                                                                    .LevelMax<TestHierarchicalDimensionA>(1)));
            yield return new TestCase<ValueWithHierarchicalDimension, ValueWithHierarchicalDimension>(
                                                                                                      "HierarchicalDimensionOptionsAFlat.json",
                                                                                                      ValueWithHierarchicalDimension.Data,
                                                                                                      b => b.GroupRowsBy(x => x.DimA)
                                                                                                            .WithHierarchicalDimensionOptions(o => o.Flatten<TestHierarchicalDimensionA>()));
            yield return new TestCase<ValueWithTwoHierarchicalDimensions, ValueWithTwoHierarchicalDimensions>(
                                                                                                              "HierarchicalDimensionTwo.json",
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

        private void ExecuteDataCubeTest<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>> builder)
        {
            var initial = PivotFactory
                          .ForDataCubes(data)
                          .WithQuerySource(new StaticDataFieldQuerySource());

            var pivotBuilder = builder(initial);

            var model = GetModel(pivotBuilder);
            model.JsonShouldMatch(SerializationService, fileName);
        }

        private void ExecuteDataCubeCountTest<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, int, int>> builder)
        {
            var initial = PivotFactory
                          .ForDataCubes(data)
                          .WithQuerySource(new StaticDataFieldQuerySource());

            var pivotBuilder = builder(initial);

            var model = GetModel(pivotBuilder);
            model.JsonShouldMatch(SerializationService, fileName);
        }

        private void ExecuteDataCubeAverageTest<TElement>(string fileName, IEnumerable<IDataCube<TElement>> data, Func<DataCubePivotBuilder<IDataCube<TElement>, TElement, TElement, TElement>, DataCubePivotBuilder<IDataCube<TElement>, TElement, (TElement sum, int count), TElement>> builder)
        {
            var initial = PivotFactory
                          .ForDataCubes(data)
                          .WithQuerySource(new StaticDataFieldQuerySource());

            var pivotBuilder = builder(initial);

            var model = GetModel(pivotBuilder);
            model.JsonShouldMatch(SerializationService, fileName);
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
}