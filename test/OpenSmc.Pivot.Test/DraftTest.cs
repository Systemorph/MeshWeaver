using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Pivot.Test;

public static class ExportExtensions
{
    // every module should have static extension
    // TODO V10: consider adding Export plugin, e.g. to have menu item (30.01.2024, Ekaterina Mishina)
    // setup export format etc, fluent builder to inject import maps etc
    public static MessageHubConfiguration AddExport(this MessageHubConfiguration configuration, Func<ExportConfigurationBuilder, ExportConfigurationBuilder> exportConf)
    {
        // exportConf - add customization here 
        return configuration
            .WithServices(services => services.AddSingleton<IExportService>())
            .AddPlugin<StandardExportPlugin>(); // register export 
    }
}

public record ExportRequest(params string[] Types); // standard export request

public class StandardExportPlugin : MessageHubPlugin<StandardExportPlugin>, IMessageHandler<ExportRequest>
{
    public StandardExportPlugin(IMessageHub hub) : base(hub)
    {

    }

    public IMessageDelivery HandleMessage(IMessageDelivery<ExportRequest> request)
    {
        // list through Types array
        return request.Processed();
    }
}

public record ExportConfigurationBuilder
{
}

public class IExportService
{

}

public record ExportMe : IRequest<ExportMeResult> // export my specific domain, functionality around menu item/button
{

}

public class ExportMeResult
{ // byte string for the file
}

public class DomainExportPlugin : MessageHubPlugin<DomainExportPlugin>, IMessageHandler<ExportMe>
{
    [Inject] private IExportService exportService;

    protected DomainExportPlugin(IMessageHub hub) : base(hub)
    {
    }


    public IMessageDelivery HandleMessage(IMessageDelivery<ExportMe> request)
    {
        Hub.Post(new ExportMeResult(), o => o.ResponseFor(request)); // query source, types => export
        return request.Processed();
    }
}

public class DraftTest : HubTestBase
{
    public DraftTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
                .AddExport(conf => conf)
                .AddPlugin<DomainExportPlugin>()
                .AddSerialization(conf => conf)
            //.AddApplicationLayout
            ;
    }

    [Fact]
    public async Task TestMe()
    {
        var client = GetClient();
        var result = await client.AwaitResponse(new ExportMe(), o => o.WithTarget(new HostAddress()));
        result.Should().BeAssignableTo<IMessageDelivery<ExportMeResult>>();

    }
}