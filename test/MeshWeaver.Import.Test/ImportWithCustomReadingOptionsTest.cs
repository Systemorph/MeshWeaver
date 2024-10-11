﻿using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.DataSetReader;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Import.Test;

public class ImportWithCustomReadingOptionsTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    nameof(GenericDataSource),
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
                        .AddImport()
            );

    private const char CustomDelimiter = ';';

    [Fact]
    public async Task SimpleCustomDelimiterTest()
    {
        const string SystemName = nameof(MyRecord.SystemName);
        const string DisplayName = nameof(MyRecord.DisplayName);
        const string Number = nameof(MyRecord.Number);
        const string StrArr = nameof(MyRecord.StringsArray);
        const string StrList = nameof(MyRecord.StringsList);
        const string IntArr = nameof(MyRecord.IntArray);

        // arrange
        const string content =
            $@"@@{nameof(MyRecord)}
{SystemName};{StrArr}0;{StrArr}1;{StrArr}2;{DisplayName};{StrList}0;{StrList}1;{StrList}2;{IntArr}0;{IntArr}1;{IntArr}2;{Number}
""{SystemName}1"";"";a1,"";""a;,2"";""a,3;"";"",{DisplayName};1"";,null;,;"";"";7;2;""19"";42";

        var client = GetClient();
        var importRequest = new ImportRequest(content)
        {
            DataSetReaderOptions = new DataSetReaderOptions().WithDelimiter(CustomDelimiter),
        };

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
            resRecord.SystemName.Should().Be($"{SystemName}1");
            resRecord.DisplayName.Should().Be($",{DisplayName};1");
            resRecord.Number.Should().Be(42);
            resRecord
                .StringsArray.Should()
                .NotBeNull()
                .And.HaveCount(3)
                .And.Equal(";a1,", "a;,2", "a,3;");
            resRecord
                .StringsList.Should()
                .NotBeNull()
                .And.HaveCount(3)
                .And.Equal(",null", ",", ";");
            resRecord.IntArray.Should().NotBeNull().And.HaveCount(3).And.Equal(7, 2, 19);
            resRecord.IntList.Should().BeNull();
        }
    }
}
