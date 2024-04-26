using DocumentFormat.OpenXml.Office2010.Excel;
using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Fixture;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Builder;
using OpenSmc.TestDomain;
using OpenSmc.TestDomain.Cubes;
using OpenSmc.TestDomain.SimpleData;
using Xunit.Abstractions;

namespace OpenSmc.Reporting.Test
{
    public static class VerifyExtension
    {
        public static async Task Verify(this object model, string fileName)
        {
            await Verifier.Verify(model).UseFileName(fileName).UseDirectory("Json");
        }
    }

    public class ReportTest : HubTestBase // PivotTest
    {
        public ReportTest(ITestOutputHelper toh)
            : base(toh) { }

        protected override MessageHubConfiguration ConfigureHost(
            MessageHubConfiguration configuration
        )
        {
            return base.ConfigureHost(configuration)
                .AddData(data =>
                    data.FromConfigurableDataSource(
                        "TestData",
                        dataSource =>
                            dataSource
                                .WithType<LineOfBusiness>(type =>
                                    type.WithInitialData(LineOfBusiness.Data)
                                )
                                .WithType<Country>(type => type.WithInitialData(Country.Data))
                                .WithType<AmountType>(type => type.WithInitialData(AmountType.Data))
                                .WithType<Scenario>(type => type.WithInitialData(Scenario.Data))
                                .WithType<Split>(type => type.WithInitialData(Split.Data))
                                .WithType<Currency>(type => type.WithInitialData(Currency.Data))
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
                                    type.WithInitialData(
                                        ValueWithAggregateByHierarchicalDimension.Data
                                    )
                                )
                                .WithType<ValueWithMixedDimensions>(type =>
                                    type.WithInitialData(ValueWithMixedDimensions.Data)
                                )
                                .WithType<CashflowElement>(type =>
                                    type.WithInitialData(
                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency()
                                    )
                                )
                                .WithType<ValueWithTwoHierarchicalDimensions>(type =>
                                    type.WithInitialData(ValueWithTwoHierarchicalDimensions.Data)
                                )
                    )
                );
        }

        private async Task<WorkspaceState> GetWorkspaceStateAsync()
        {
            var workspace = GetHost().GetWorkspace();
            await workspace.Initialized;
            return workspace.State;
        }

        [Theory]
        [MemberData(nameof(ReportCases))]
        public async Task ReportWithGridOptions<T>(
            string fileName,
            IEnumerable<T> data,
            Func<PivotBuilder<T, T, T>, ReportBuilder<T, T, T>> toReportBuilder
        )
        {
            var initialPivotBuilder = PivotFactory
                .ForObjects(data)
                .WithState(await GetWorkspaceStateAsync());

            var reportBuilder = toReportBuilder(initialPivotBuilder);

            var gridOptions = GetModel(reportBuilder);

            await gridOptions.Verify(fileName);
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
            var initialPivotBuilder = PivotFactory
                .ForDataCubes(data)
                .WithState(await GetWorkspaceStateAsync());

            var reportBuilder = toReportBuilder(initialPivotBuilder);

            var gridOptions = GetModel(reportBuilder);
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
                        .WithOptions(rm =>
                            rm.HideRowValuesForDimension("DimA", x => x.ForLevel(0, 1))
                        )
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

        public static IEnumerable<object[]> ReportCases()
        {
            yield return new ReportTestCase<CashflowElement>(
                "EmptyReport.json",
                Enumerable.Empty<CashflowElement>(),
                x => x.GroupRowsBy(y => y.AmountType).ToTable()
            );

            yield return new ReportTestCase<CashflowElement>(
                "FormattedReportModelSingleCurrency.json",
                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                x =>
                    x.GroupColumnsBy(y => y.LineOfBusiness)
                        .GroupRowsBy(y => y.AmountType)
                        .ToTable()
                        .WithOptions(rm =>
                            rm.WithColumns(cols =>
                                    cols.Modify(
                                        "Value",
                                        c => c.WithDisplayName("Amount").Highlighted()
                                    )
                                )
                                .WithRows(rows =>
                                    rows.Modify(
                                        r => r.RowGroup.DisplayName == "Premium",
                                        r => r.WithDisplayName("Total Premium").AsTotal()
                                    )
                                )
                        )
            );
            yield return new ReportTestCase<CashflowElement>(
                "HideAggregationForRows.json",
                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                x =>
                    x.GroupColumnsBy(y => y.Split)
                        .GroupRowsBy(y => y.AmountType)
                        .GroupRowsBy(y => y.LineOfBusiness)
                        .ToTable()
                        .WithOptions(rm => rm.HideRowValuesForDimension("AmountType"))
            );
            yield return new ReportTestCase<CashflowElement>(
                "FormattedReportModelSingleCurrency1.json",
                CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency(),
                x =>
                    x.GroupColumnsBy(y => y.LineOfBusiness)
                        .GroupRowsBy(y => y.AmountType)
                        .ToTable()
                        .WithOptions(rm =>
                            rm.WithColumns(cols =>
                                cols.Modify("Value", c => c.WithDisplayName("Amount").Highlighted())
                            )
                        )
                        .WithOptions(rm =>
                            rm.WithRows(rows =>
                                rows.Modify(
                                    r => r.RowGroup.DisplayName == "Premium",
                                    r => r.WithDisplayName("Total Premium").AsTotal()
                                )
                            )
                        )
            );
            yield return new ReportTestCase<CashflowElement>(
                "FormattedReportModel.json",
                CashflowFactory.GenerateEquallyWeightedAllPopulated(),
                x =>
                    x.GroupColumnsBy(y => y.LineOfBusiness)
                        .GroupRowsBy(y => y.AmountType)
                        .ToTable()
                        .WithOptions(rm =>
                            rm.WithColumns(cols =>
                                    cols.Modify(
                                        "Value",
                                        c => c.WithDisplayName("Amount").Highlighted()
                                    )
                                )
                                .WithRows(rows =>
                                    rows.Modify(
                                        r => r.RowGroup.DisplayName == "Premium",
                                        r => r.WithDisplayName("Total Premium").AsTotal()
                                    )
                                )
                        )
            );
        }

        private class ReportTestCase<T>
        {
            private readonly IEnumerable<T> data;
            private readonly Func<PivotBuilder<T, T, T>, ReportBuilder<T, T, T>> pivotBuilder;
            private readonly string benchmarkFile;

            public static implicit operator object[](ReportTestCase<T> testCase) =>
                new object[] { testCase.benchmarkFile, testCase.data, testCase.pivotBuilder };

            public ReportTestCase(
                string benchmarkFile,
                IEnumerable<T> data,
                Func<PivotBuilder<T, T, T>, ReportBuilder<T, T, T>> pivotBuilder
            )
            {
                this.data = data;
                this.pivotBuilder = pivotBuilder;
                this.benchmarkFile = benchmarkFile;
            }
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

        protected object GetModel<T, TIntermediate, TAggregate>(
            PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder
        )
        {
            return pivotBuilder.ToTable().WithOptions(o => o.AutoHeight()).Execute();
        }

        private protected object GetModel<T, TIntermediate, TAggregate>(
            ReportBuilder<T, TIntermediate, TAggregate> reportBuilder
        )
        {
            return reportBuilder.WithOptions(o => o.AutoHeight()).Execute();
        }

        protected object GetModel<TElement, TIntermediate, TAggregate>(
            DataCubePivotBuilder<
                IDataCube<TElement>,
                TElement,
                TIntermediate,
                TAggregate
            > pivotBuilder
        )
        {
            return pivotBuilder.ToTable().WithOptions(o => o.AutoHeight()).Execute();
        }

        private object GetModel<T, TIntermediate, TAggregate>(
            DataCubeReportBuilder<IDataCube<T>, T, TIntermediate, TAggregate> reportBuilder
        )
        {
            return reportBuilder.WithOptions(o => o.AutoHeight()).Execute();
        }
    }
}
