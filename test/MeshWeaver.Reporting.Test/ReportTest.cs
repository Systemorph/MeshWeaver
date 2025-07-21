using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Reporting.Builder;
using MeshWeaver.TestDomain;
using MeshWeaver.TestDomain.Cubes;
using VerifyXunit;
using Xunit;

namespace MeshWeaver.Reporting.Test
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
                    data.AddSource(
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
                                .WithType<CashflowElement>(type =>
                                    type.WithInitialData(
                                        CashflowFactory.GenerateEquallyWeightedAllPopulatedSingleCurrency()
                                    )
                                )
                    )
                );
        }

        [Theory]
        [MemberData(nameof(ReportCases))]
        public async Task ReportWithGridOptions<T>(
            string fileName,
            IEnumerable<T> data,
            Func<PivotBuilder<T, T, T>, ReportBuilder<T, T, T>> toReportBuilder
        )
        {
            var initialPivotBuilder = GetHost().GetWorkspace()
                .Pivot(data)
                ;

            var reportBuilder = toReportBuilder(initialPivotBuilder);

            var gridOptions = await GetModelAsync(reportBuilder);

            await gridOptions.Verify(fileName);
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
                                        r => r.RowGroup?.DisplayName == "Premium",
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
                                    r => r.RowGroup?.DisplayName == "Premium",
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
                                        r => r.RowGroup?.DisplayName == "Premium",
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
            DataCubePivotBuilder<
                IDataCube<TElement>,
                TElement,
                TIntermediate,
                TAggregate
            > pivotBuilder
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
}
