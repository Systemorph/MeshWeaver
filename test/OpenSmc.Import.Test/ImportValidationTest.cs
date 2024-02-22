using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.TestDomain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
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
                data => data.WithDataSource
                (
                    nameof(DataSource),
                    source => source
                            .ConfigureCategory(TestDomain.ContractDomain)
                )
            )
            .AddImport(import => import
                .WithFormat("Test1", format => format
                    .WithAutoMappings()
                    .WithValidation((_, _) => false)
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
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);
        importResponse.Message.Log.Messages.OfType<LogMessage>()
            .Where(x => x.LogLevel == LogLevel.Error)
            .Select(x => x.Message).Should()
            .BeEquivalentTo("The field FoundationYear must be between 1999 and 2023.",
                ImportPlugin.ValidationStageFailed);

        var ret = await client.AwaitResponse(new GetManyRequest<TestDomain.Contract>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(0);
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
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);

        importResponse.Message.Log.Messages.OfType<LogMessage>().Should()
            .ContainSingle(x => x.LogLevel == LogLevel.Error)
            .Which.Message.Should().Be(ImportPlugin.ValidationStageFailed);

        var ret = await client.AwaitResponse(new GetManyRequest<TestDomain.Country>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(0);
    }

    //        [Fact]
    //        public async Task ImportWithValidationRuleTest()
    //        {
    //            const string content = @"@@Address
    //Street,Country
    //Red,RU
    //Blue,FR";
    //            var streetCanNotBeRed = "Street can not be Red";

    //            CategoryService.Configure(nameof(Country), b => b.WithDisplayName(nameof(Country))
    //                                                             .WithCompleteCategoryQuery(() => new[]
    //                                                                                              {
    //                                                                                                  new ImportTestDomain.Country { DisplayName = "FR", SystemName = "FR" },
    //                                                                                                  new ImportTestDomain.Country { DisplayName = "RU", SystemName = "RU" }
    //                                                                                              }));

    //            var log = await ImportVariable.FromString(content)
    //                                          .WithType<ImportTestDomain.Address>()
    //                                          .WithValidation((instance, _) =>
    //                                                          {
    //                                                              var ret = true;
    //                                                              if (instance is ImportTestDomain.Address address && address.Street == "Red")
    //                                                              {
    //                                                                  ActivityService.LogError(streetCanNotBeRed);
    //                                                                  ret = false;
    //                                                              }
    //                                                              return ret;
    //                                                          })
    //                                          .ExecuteAsync();

    //            log.Messages.Select(x => x.Message).Should().BeEquivalentTo(streetCanNotBeRed, string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Address).FullName));

    //            var ret = Workspace.GetItems<ImportTestDomain.Address>();
    //            ret.Should().HaveCount(0);
    //            var countries = Workspace.GetItems<ImportTestDomain.Country>();
    //            countries.Should().HaveCount(0);
    //        }

    //        [Fact]
    //        public async Task ImportWithDefaultValidationRuleTest()
    //        {
    //            const string content = @"@@Address
    //Street,Country
    //Red,RU
    //Blue,FR";
    //            var streetCanNotBeRed = "Street can not be Red";

    //            CategoryService.Configure(nameof(Country), b => b.WithDisplayName(nameof(Country))
    //                                                             .WithCompleteCategoryQuery(() => new[]
    //                                                                                              {
    //                                                                                                  new ImportTestDomain.Country { DisplayName = "FR", SystemName = "FR" },
    //                                                                                                  new ImportTestDomain.Country { DisplayName = "RU", SystemName = "RU" }
    //                                                                                              }));

    //            ImportVariable.SetDefaultValidation((instance, _) =>
    //                                                {
    //                                                    var ret = true;

    //                                                    if (instance is ImportTestDomain.Address address && address.Street == "Red")
    //                                                    {
    //                                                        ActivityService.LogError(streetCanNotBeRed);
    //                                                        ret = false;
    //                                                    }
    //                                                    return ret;
    //                                                });

    //            var log = await ImportVariable.FromString(content).WithType<ImportTestDomain.Address>().ExecuteAsync();

    //            log.Messages.Select(x=>x.Message).Should().BeEquivalentTo(streetCanNotBeRed, string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Address).FullName));

    //            var ret = Workspace.GetItems<ImportTestDomain.Address>();
    //            ret.Should().HaveCount(0);
    //            var countries = Workspace.GetItems<ImportTestDomain.Country>();
    //            countries.Should().HaveCount(0);
    //        }

    //        [Fact]
    //        public async Task ImportWithValidationAndDefaultValidationRulesTest()
    //        {
    //            const string content = @"@@Address
    //Street,Country
    //Red,RU
    //Blue,FR";
    //            var streetCanNotBeRed = "Street can not be Red";
    //            var streetCanNotBeBlue = "Street can not be Red";

    //            CategoryService.Configure(nameof(Country), b => b.WithDisplayName(nameof(Country))
    //                                                             .WithCompleteCategoryQuery(() => new[]
    //                                                                                              {
    //                                                                                                  new ImportTestDomain.Country { DisplayName = "FR", SystemName = "FR" },
    //                                                                                                  new ImportTestDomain.Country { DisplayName = "RU", SystemName = "RU" }
    //                                                                                              }));

    //            ImportVariable.SetDefaultValidation((instance, _) =>
    //                                                {
    //                                                    var ret = true;

    //                                                    if (instance is ImportTestDomain.Address address && address.Street == "Blue")
    //                                                    {
    //                                                        ActivityService.LogError(streetCanNotBeBlue);
    //                                                        ret = false;
    //                                                    }
    //                                                    return ret;
    //                                                });

    //            var log = await ImportVariable.FromString(content)
    //                                          .WithType<ImportTestDomain.Address>()
    //                                          .WithValidation((instance, _) =>
    //                                                          {
    //                                                              var ret = true;

    //                                                              if (instance is ImportTestDomain.Address address && address.Street == "Red")
    //                                                              {
    //                                                                  ActivityService.LogError(streetCanNotBeRed);
    //                                                                  ret = false;
    //                                                              }
    //                                                              return ret;
    //                                                          })
    //                                          .ExecuteAsync();

    //            var errors = log.Messages.ToArray();
    //            errors.Should().HaveCount(3);
    //            errors.Select(x => x.Message).Should().BeEquivalentTo(streetCanNotBeBlue, streetCanNotBeRed, string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Address).FullName));

    //            var ret = Workspace.GetItems<ImportTestDomain.Address>();
    //            ret.Should().HaveCount(0);
    //            var countries = Workspace.GetItems<ImportTestDomain.Country>();
    //            countries.Should().HaveCount(0);
    //        }


    //        [Fact]
    //        public async Task ImportOfPercentageTest()
    //        {
    //            const string content = @"@@Discount
    //DoubleValue,Country
    //0.4,14,2,0";

    //            var log = await ImportVariable.FromString(content)
    //                                          .WithType<ImportTestDomain.Discount>()
    //                                          .ExecuteAsync();

    //            var errors = log.Errors().Select(x => x.Message).ToArray();
    //            errors.Should().BeEquivalentTo("The IntValue field must have type from these: System.Double, System.Decimal, System.Single.",
    //                                           "The DecimalValue field value should be in interval from 10 to 20.", 
    //                                           string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Discount).FullName));

    //            var ret = Workspace.GetItems<ImportTestDomain.Discount>();
    //            ret.Should().HaveCount(0);
    //        }


    //        [Fact]
    //        public async Task ImportWithSaveLogTest()
    //        {
    //            const string content = @"@@Country
    //SystemName,DisplayName
    //A,B";

    //            var log1 = await ImportVariable.FromString(content)
    //                                          .WithType<ImportTestDomain.Country>()
    //                                          .SaveLogs()
    //                                          .ExecuteAsync();

    //            log1.Status.Should().Be(ActivityLogStatus.Succeeded);
    //            Workspace.GetItems<ActivityLog>().Should().ContainSingle().Which.Should().Be(log1);

    //            //save failed log
    //            var log2 = await ImportVariable.FromString(content)
    //                                          .WithType<ImportTestDomain.Country>()
    //                                          .WithValidation((_,_)=>
    //                                                          {
    //                                                              ActivityService.LogError("someError");
    //                                                              return false;
    //                                                          })
    //                                          .SaveLogs()
    //                                          .ExecuteAsync();

    //            log2.Status.Should().Be(ActivityLogStatus.Failed);
    //            Workspace.GetItems<ActivityLog>().Should().HaveCount(2);
    //            Workspace.GetItems<ActivityLog>().Where(x=>x.Status == ActivityLogStatus.Failed).Should().ContainSingle().Which.Should().Be(log2);
    //        }
}
