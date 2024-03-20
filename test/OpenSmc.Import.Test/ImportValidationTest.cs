using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.TestDomain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using System.Reactive.Linq;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test;

public class ImportValidationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StreetCanNotBeRed = "Street can not be Red";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        var activityService = ServiceProvider.GetService<IActivityService>();
        return base.ConfigureHost(configuration)
            .AddData(
                data => data.FromConfigurableDataSource
                (
                    nameof(DataSource),
                    source => source
                            .ConfigureCategory(TestDomain.ContractDomain)
                            .WithType<ActivityLog>()
                )
            )
            .WithHostedHub(new TestDomain.ImportAddress(configuration.Address),
                config => config
                    .AddImport(
                        data => data.FromHub(
                            configuration.Address,
                            source => source
                                .ConfigureCategory(TestDomain.ContractDomain)
                                .WithType<ActivityLog>()
                            ),
                        import => import
                        .WithFormat("Test1", format => format
                            .WithAutoMappings()
                            .WithValidation((_, _) => false)
                            .SaveLogs()
                        )
                        .WithFormat("Test2", format => format
                            .WithAutoMappings()
                            .WithValidation((instance, _) =>
                            {
                                var ret = true;
                                if (instance is TestDomain.Address address && address.Street == "Red")
                                {
                                    activityService.LogError(StreetCanNotBeRed);
                                    ret = false;
                                }

                                return ret;
                            })
                        )
                    )
            );
    }

    [Fact]
    public async Task SimpleValidationAttributeTest()
    {
        const string content = @"@@Contract
FoundationYear,ContractType
1900,Fixed-Price
2020,Cost-Plus";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new TestDomain.ImportAddress(new HostAddress())));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);
        importResponse.Message.Log.Messages.OfType<LogMessage>()
            .Where(x => x.LogLevel == LogLevel.Error)
            .Select(x => x.Message).Should()
            .BeEquivalentTo("The field FoundationYear must be between 1999 and 2023.",
                ImportPlugin.ValidationStageFailed);

        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<TestDomain.Contract>().FirstAsync();

        ret.Should().HaveCount(0);
    }

    [Fact]
    public async Task ImportWithSimpleValidationRuleTest()
    {
        const string content = @"@@Country
    SystemName,DisplayName
    RU,Russia
    FR,France";

        var client = GetClient();
        var importRequest = new ImportRequest(content) {Format = "Test1"};
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new TestDomain.ImportAddress(new HostAddress())));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);

        importResponse.Message.Log.Messages.Should()
            .ContainSingle(x => x.LogLevel == LogLevel.Error)
            .Which.Message.Should().Be(ImportPlugin.ValidationStageFailed);
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<TestDomain.Country>().FirstAsync();


        ret.Should().HaveCount(0);
    }

    [Fact]
    public async Task ImportOfPercentageTest()
    {
        const string content = @"@@Discount
    DoubleValue,Country
    0.4,14,2,0";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new TestDomain.ImportAddress(new HostAddress())));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);
        importResponse.Message.Log.Messages.OfType<LogMessage>()
            .Where(x => x.LogLevel == LogLevel.Error)
            .Select(x => x.Message).Should()
            .BeEquivalentTo("The IntValue field must have type from these: System.Double, System.Decimal, System.Single.",
                "The DecimalValue field value should be in interval from 10 to 20.",
                ImportPlugin.ValidationStageFailed);

        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<TestDomain.Discount>().FirstAsync();


        ret.Should().HaveCount(0);
    }

    [Fact]
    public async Task ImportWithSaveLogTest()
    {
        const string content = @"@@Country
    SystemName,DisplayName
    A,B";

        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = "Test1" };
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new TestDomain.ImportAddress(new HostAddress())));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);

        importResponse.Message.Log.Messages.Should()
            .ContainSingle(x => x.LogLevel == LogLevel.Error)
            .Which.Message.Should().Be(ImportPlugin.ValidationStageFailed);

        await Task.Delay(300);

        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var ret = await workspace.GetObservable<ActivityLog>().FirstAsync();

        ret.Should().HaveCount(1);
        //var log = ret.Message.Items.First();
        //log.Status.Should().Be(ActivityLogStatus.Failed);
    }
}
