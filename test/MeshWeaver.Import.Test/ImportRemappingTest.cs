﻿using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.DataStructures;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Import.Test;

public class ImportRemappingTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string RemappingTestFormat = nameof(RemappingTestFormat);

    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    nameof(GenericUnpartitionedDataSource),
                    source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                )
            )
            .WithHostedHub(
                new TestDomain.ImportAddress(configuration.Address),
                config =>
                    config
                        .AddData(data =>
                            data.FromHub(
                                configuration.Address,
                                source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                            )
                        )
                        .AddImport(import =>
                            import
                            //// TODO V10: There is no way to override behavior for Default format (2024/02/15, Dmitry Kalabin)
                            //.WithFormat(ImportFormat.Default,
                            //    format => format.WithAutoMappings(ti => ti.WithTableMapping(nameof(MyRecord), MapMyRecord))
                            //)
                            .WithFormat(
                                RemappingTestFormat,
                                format =>
                                    format.WithMappings(ti =>
                                        ti.WithTableMapping(nameof(MyRecord), MapMyRecord)
                                    )
                            )
                        )
            );

    private Task<IReadOnlyCollection<object>> MapMyRecord(IDataSet set, IDataTable table)
        => Task.FromResult<IReadOnlyCollection<object>>(MapMyRecordInd(set, table).ToArray());
    private IEnumerable<object> MapMyRecordInd(IDataSet set, IDataTable table)
    {
        const string systemNameColumn = Prefix + nameof(MyRecord.SystemName);
        const string displayNameColumn = nameof(MyRecord.DisplayName);
        const string strArrColumn = Prefix + nameof(MyRecord.StringsArray);
        const string strListColumn = nameof(MyRecord.StringsList);
        const string intListColumn = Prefix + nameof(MyRecord.IntList);

        foreach (var row in table)
        {
            yield return new MyRecord()
            {
                SystemName = row[$"{systemNameColumn}"]?.ToString(),
                DisplayName = row[$"{displayNameColumn}"]?.ToString(),
                StringsArray = Enumerable
                    .Range(0, 3)
                    .Select(i => row[$"{strArrColumn}{i}"])
                    .Where(x => x is not null)
                    .Select(x => x.ToString())
                    .ToArray(),
                StringsList = Enumerable
                    .Range(0, 3)
                    .Select(i => row[$"{strListColumn}{i}"])
                    .Where(x => x is not null)
                    .Select(x => x.ToString())
                    .ToList(),
                IntList = Enumerable
                    .Range(0, 3)
                    .Select(i => row[$"{intListColumn}{i}"])
                    .Where(x => x is not null)
                    .Select(x => int.Parse(x.ToString()))
                    .ToList(),
            };
        }
    }

    private const string Prefix = "Prefix_";

    [Fact]
    public async Task SimpleRemappingPropertyTest()
    {
        const string systemName = nameof(MyRecord.SystemName);
        const string displayName = nameof(MyRecord.DisplayName);
        const string strArr = nameof(MyRecord.StringsArray);
        const string strList = nameof(MyRecord.StringsList);
        const string intList = nameof(MyRecord.IntList);

        // arrange
        const string content =
            $@"@@{nameof(MyRecord)}
{Prefix + systemName},{Prefix + strArr}0,{Prefix + strArr}1,{Prefix + strArr}2,{displayName},{strList}0,{strList}1,{strList}2,{Prefix + intList}0,{Prefix + intList}1,{Prefix + intList}2
""{systemName}1"",""a1"",""a2"",""a3"",""{displayName}1"",null,,"""",5,8,""""";

        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = RemappingTestFormat, };

        // act
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress(new HostAddress()))
        );

        // assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress(new HostAddress()))
            .GetWorkspace();
        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        var resRecord = ret.Should().ContainSingle().Which;
        resRecord.Should().NotBeNull();

        using (new AssertionScope())
        {
            resRecord.SystemName.Should().Be($"{systemName}1");
            resRecord.DisplayName.Should().Be($"{displayName}1");
            resRecord.Number.Should().Be(default);
            resRecord
                .StringsArray.Should()
                .NotBeNull()
                .And.HaveCount(3)
                .And.Equal("a1", "a2", "a3");
            resRecord
                .StringsList.Should()
                .NotBeNull()
                .And.ContainSingle()
                .Which.Should()
                .Be("null");
            resRecord.IntArray.Should().BeNull();
            resRecord.IntList.Should().NotBeNull().And.HaveCount(2).And.Equal(5, 8);
        }
    }
}
