using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.Domain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test;

public class ImportValidationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {

        return base.ConfigureHost(configuration)
                .AddData(
                    data => data.WithDataSource
                    (
                        nameof(DataSource),
                        source => source
                            .ConfigureCategory(TestDomain.ContractDomain)
                    )
                )
                .AddImport(import => import)
            ;
    }


        [Fact]
        public async Task SimpleValidationAttributeTest()
        {
            const string content = @"@@Contract
FoundationYear,ContractType
1900,Fixed-Price
2020,Cost-Plus";

        //CategoryService.Configure("ContractType", b => b.WithDisplayName("ContractType")
        //                                                 .WithCompleteCategoryQuery(() => new[]
        //                                                                                  {
        //                                                                                      "Fixed-Price"
        //                                                                                  }));

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Failed);
        //var logObject = importResponse.Message.Log.Messages.OfType<LogMessage>().Should().ContainSingle().Which;
        //logObject.Object.Should().BeOfType<ValidationResult>();
        //logObject.Message.Should().Be("The field FoundationYear must be between 1999 and 2023.");
        //log.Messages.OfTypeOnly<LogMessage>().Select(x => x.Message)
        //   .Should().BeEquivalentTo(string.Format(CategoryAttributeValidator.UnknownValueErrorMessage, nameof(ImportTestDomain.Contract.ContractType), typeof(ImportTestDomain.Contract).FullName, "Cost-Plus"),
        //                            string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Contract).FullName));

        var ret = await client.AwaitResponse(new GetManyRequest<TestDomain.Contract>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(0);

        //var log = await ImportVariable.FromString(content).WithType<ImportTestDomain.Contract>().ExecuteAsync();
        //    log.Status.Should().Be(ActivityLogStatus.Failed);
        //    log.Errors().Should().HaveCount(3);
        //    log.Messages.Should().HaveCount(3);
        //    var logObject = log.Messages.OfType<LogMessageObject>().Should().ContainSingle().Which;
        //    logObject.Object.Should().BeOfType<ValidationResult>();
        //    logObject.Message.Should().Be("The field FoundationYear must be between 1999 and 2023.");
        //    log.Messages.OfTypeOnly<LogMessage>().Select(x => x.Message)
        //       .Should().BeEquivalentTo(string.Format(CategoryAttributeValidator.UnknownValueErrorMessage, nameof(ImportTestDomain.Contract.ContractType), typeof(ImportTestDomain.Contract).FullName, "Cost-Plus"),
        //                                string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Contract).FullName));

        //    var ret = Workspace.GetItems<ImportTestDomain.Contract>();
        //    ret.Should().HaveCount(0);


        //    CategoryService.Configure("ContractType", b => b.WithDisplayName("ContractType")
        //                                                     .WithCompleteCategoryQuery(() => new[]
        //                                                                                      {
        //                                                                                          "Fixed-Price", "Cost-Plus"
        //                                                                                      }));

        //    log = await ImportVariable.FromString(content).WithType((_, dr) => new Contract { ContractType = dr["ContractType"]?.ToString(), FoundationYear = 2023 }).ExecuteAsync();
        //    log.Status.Should().Be(ActivityLogStatus.Succeeded);

        //    ret = Workspace.GetItems<Contract>();
        //    ret.Should().HaveCount(2);
        }

//        [Fact]
//        public async Task ImportDimensionsAndInstancesWithNoCategorySetTest()
//        {
//            const string content = @"@@Contract
//FoundationYear,ContractType
//1900,Fixed-Price
//2020,Cost-Plus";

//            //this does not work, category must be configured with proper data in it
//            var log = await ImportVariable.FromString(content).WithType<Contract>().ExecuteAsync();
//            log.Status.Should().Be(ActivityLogStatus.Failed);

//            log.Errors().Should().HaveCount(4);
//            log.Messages.Should().HaveCount(4);
//            log.Messages.Select(x => x.Message).Should().BeEquivalentTo("The field FoundationYear must be between 1999 and 2023.", 
//                                                                        string.Format(CategoryAttributeValidator.MissingCategoryErrorMessage, nameof(Contract.ContractType)),
//                                                                        string.Format(CategoryAttributeValidator.MissingCategoryErrorMessage, nameof(Contract.ContractType)),
//                                                                        string.Format(MappingService.ValidationStageFailed, typeof(Contract).FullName));

//            var ret = Workspace.GetItems<Contract>();
//            ret.Should().HaveCount(0);
//        }

//        [Fact]
//        public async Task ImportWithSimpleValidationRuleTest()
//        {
//            const string content = @"@@Country
//SystemName,DisplayName
//RU,Russia
//FR,France";

//            var log = await ImportVariable.FromString(content)
//                                          .WithType<ImportTestDomain.Country>()
//                                          .WithValidation((_, _) => false)
//                                          .ExecuteAsync();

//            log.Messages.Should().ContainSingle().Which.Message.Should().Be(string.Format(MappingService.ValidationStageFailed, typeof(ImportTestDomain.Country).FullName));

//            var ret = Workspace.GetItems<ImportTestDomain.Country>();
//            ret.Should().HaveCount(0);
//        }

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
