using System.Reactive.Linq;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Import.Implementation;
using MeshWeaver.Messaging;
using Xunit;
using System.Linq;
using System.Threading.Tasks;

namespace MeshWeaver.Import.Test;

public class ImportValidationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StreetCanNotBeRed = "Street can not be Red";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    source =>
                        source.ConfigureCategory(TestDomain.ContractDomain).WithType<ActivityLog>()
                )
            );
    }

    protected override MessageHubConfiguration ConfigureRouter(MessageHubConfiguration conf)
    {
        return base.ConfigureRouter(conf).WithHostedHub(
                new TestDomain.ImportAddress(),
                config =>
                    config
                        .AddData(data =>
                            data.AddHubSource(
                                new HostAddress(),
                                source =>
                                    source
                                        .ConfigureCategory(TestDomain.ContractDomain)
                                        .WithType<ActivityLog>()
                            )
                        )
                        .AddImport(import =>
                            import
                                .WithFormat(
                                    "Test1",
                                    format =>
                                        format.WithAutoMappings().WithValidation((_, _, _) => false)
                                )
                                .WithFormat(
                                    "Test2",
                                    format =>
                                        format
                                            .WithAutoMappings()
                                            .WithValidation(
                                                (instance, _, activity) =>
                                                {
                                                    var ret = true;
                                                    if (
                                                        instance is TestDomain.StreetAddress address
                                                        && address.Street == "Red"
                                                    )
                                                    {
                                                        activity
                                                            .LogError(StreetCanNotBeRed);
                                                        ret = false;
                                                    }

                                                    return ret;
                                                }
                                            )
                                )
                        )
            );
    }

    [Fact]
    public async Task SimpleValidationAttributeTest()
    {
        const string content =
            @"@@Contract
SystemName,FoundationYear,ContractType
1,1900,Fixed-Price
2,2020,Cost-Plus";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);
        importResponse
            .Message.Log.Messages
            .Where(x => x.LogLevel == LogLevel.Error)
            .Select(x => x.Message)
            .Should()
            .BeEquivalentTo(
                "The field FoundationYear must be between 1999 and 2023.",
                ImportManager.ImportFailed
            );

        var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<TestDomain.Contract>().FirstAsync();

        ret.Should().HaveCount(0);
    }

    [Fact]
    public async Task ImportWithSimpleValidationRuleTest()
    {
        const string Content =
            @"@@Country
SystemName,DisplayName
RU,Russia
FR,France";

        var client = GetClient();
        var importRequest = new ImportRequest(Content) { Format = "Test1", SaveLog = true };
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);

        importResponse
            .Message.Log.Messages.Should()
            .ContainSingle(x => x.LogLevel == LogLevel.Error)
            .Which.Message.Should()
            .Be(ImportManager.ImportFailed);
        var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<TestDomain.Country>().FirstAsync();

        ret.Should().HaveCount(0);
    }

    [Fact]
    public async Task ImportOfPercentageTest()
    {
        const string content =
            @"@@Discount
DoubleValue,Country
0.4,14,2,0";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);
        importResponse
            .Message.Log.Messages
            .Where(x => x.LogLevel == LogLevel.Error)
            .Select(x => x.Message)
            .Should()
            .BeEquivalentTo(
                "The IntValue field must have type from these: System.Double, System.Decimal, System.Single.",
                "The DecimalValue field value should be in interval from 10 to 20.",
                ImportManager.ImportFailed
            );

        var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<TestDomain.Discount>().FirstAsync();

        ret.Should().HaveCount(0);
    }

    [Fact]
    public async Task ImportWithSaveLogTest()
    {
        const string content =
            @"@@Country
SystemName,DisplayName
A,B";

        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = "Test1", SaveLog = true };
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);

        importResponse
            .Message.Log.Messages.Should()
            .ContainSingle(x => x.LogLevel == LogLevel.Error)
            .Which.Message.Should()
            .Be(ImportManager.ImportFailed);

        //await Task.Delay(300);

        //var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        //var ret = await workspace.GetObservable<ActivityLog>()
        //    .Timeout(10.Seconds())
        //    .FirstAsync(x => x.Any());

        //ret.Should().HaveCount(1);
        //var log = ret.Message.Items.First();
        //log.Status.Should().Be(ActivityLogStatus.Failed);
    }

    [Fact(Skip = "Currently not implemented funcitonality")]
    public async Task ImportWithCategoryValidationTest()
    {
        const string content =
            @"@@Country
SystemName,DisplayName
RU,Russia
FR,France
@@Address
Street,Country
Red,RU
Blue,FR";

        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = "Test2" };
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);

        importResponse
            .Message.Log.Messages
            .Should()
            .ContainSingle(x => x.LogLevel == LogLevel.Error)
            .Which.Message.Should()
            .Be(ImportManager.ImportFailed);

        var workspace = client.GetWorkspace();

        var ret = await workspace.GetObservable<TestDomain.Country>().FirstAsync();

        ret.Should().HaveCount(0);
    }
}
