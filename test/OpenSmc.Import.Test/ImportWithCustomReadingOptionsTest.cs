using FluentAssertions.Execution;
using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataSetReader;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;
using OpenSmc.Data.TestDomain;

namespace OpenSmc.Import.Test;

public class ImportWithCustomReadingOptionsTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) 
        => base.ConfigureHost(configuration)
            .AddData(
                data => data.WithDataSource(nameof(DataSource),
                    source => source
                        .ConfigureCategory(TestDomain.TestRecordsDomain)
                )
            )
            .AddImport(import => import
            )
        ;

    private const char CustomDelimiter = ';';

    [Fact]
    public async Task SimpleCustomDelimiterTest()
    {
        const string systemName = nameof(MyRecord.SystemName);
        const string displayName = nameof(MyRecord.DisplayName);
        const string number = nameof(MyRecord.Number);
        const string strArr = nameof(MyRecord.StringsArray);
        const string strList = nameof(MyRecord.StringsList);
        const string intArr = nameof(MyRecord.IntArray);

        // arrange
        const string content = $@"@@{nameof(MyRecord)}
{systemName};{strArr}0;{strArr}1;{strArr}2;{displayName};{strList}0;{strList}1;{strList}2;{intArr}0;{intArr}1;{intArr}2;{number}
""{systemName}1"";"";a1,"";""a;,2"";""a,3;"";"",{displayName};1"";,null;,;"";"";7;2;""19"";42";


        var client = GetClient();
        var importRequest = new ImportRequest(content) { DataSetReaderOptions = new DataSetReaderOptions().WithDelimiter(CustomDelimiter), };

        // act
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));

        // assert
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(), o => o.WithTarget(new HostAddress()));
        var resRecord = ret.Message.Items.Should().ContainSingle().Which;
        resRecord.Should().NotBeNull();

        using (new AssertionScope())
        {
            resRecord.SystemName.Should().Be($"{systemName}1");
            resRecord.DisplayName.Should().Be($",{displayName};1");
            resRecord.Number.Should().Be(42);
            resRecord.StringsArray.Should().NotBeNull().And.HaveCount(3).And.Equal(";a1,", "a;,2", "a,3;");
            resRecord.StringsList.Should().NotBeNull().And.HaveCount(3).And.Equal(",null", ",", ";");
            resRecord.IntArray.Should().NotBeNull().And.HaveCount(3).And.Equal(7, 2, 19);
            resRecord.IntList.Should().BeNull();
        }
    }
}
