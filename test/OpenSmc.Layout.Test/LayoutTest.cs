using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Layout.LayoutClient;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Inject] private ILogger<LayoutTest> logger;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data.FromConfigurableDataSource("Local", 
                ds => ds
                    .WithType<TestLayout.DataRecord>(t => t.WithInitialData([new("Hello", "World")]))))
            .AddPlugin<TestLayout>()    
            .AddLayout(layout => layout.Hub.ServiceProvider.GetRequiredService<TestLayout>().Configure(layout));

    }


    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient(new HostAddress());
    }


    [Fact]
    public async Task LayoutStackUpdateTest()
    {
        var client = GetClient();
        var area = await client.GetAreaAsync(state => state.GetById(TestLayout.MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().BeEmpty();
        await client.ClickAsync(_ => area);

        await client.GetAreaAsync(state => state.GetById("HelloId"));
        area = await client.GetAreaAsync(state => state.GetById(TestLayout.MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().HaveCount(1);

    }
    [Fact]
    public async Task GetPredefinedArea()
    {
        var client = GetClient();
        client.Post(new RefreshRequest { Area = TestLayout.NamedArea }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetByIdAndArea(TestLayout.MainStackId, TestLayout.NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayout.NamedArea);
        area = await client.GetAreaAsync(state => state.GetById(TestLayout.NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayout.NamedArea);
        var address = ((IUiControl)area.View).Address;
        area = await client.GetAreaAsync(state => state.GetByAddress(address));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayout.NamedArea);

    }



    [Fact]
    public async Task UpdatingView()
    {

        var client = GetClient();
        client.Post(new RefreshRequest { Area = TestLayout.UpdatingView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetById(TestLayout.UpdatingView));
        area.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(TestLayout.SomeString);

        await client.ClickAsync(_ => area);

        AreaChangedEvent IsUpdatedView(LayoutClientState layoutClientState)
        {
            var ret = layoutClientState.GetById(TestLayout.UpdatingView);
            if (ret?.View is TextBoxControl { Data: not TestLayout.SomeString })
                return ret;

            logger.LogInformation($"Found view: {ret?.View}");
            return null;
        }

        var changedArea = await client.GetAreaAsync(IsUpdatedView);
        changedArea.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(TestLayout.NewString);


    }
    [Fact]
    public async Task DataBoundView()
    {

        var client = GetClient();
        client.Post(new RefreshRequest { Area = TestLayout.DataBoundView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetById(TestLayout.DataBoundView));
        area.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().BeOfType<Binding>()
            .Which.Path.Should().Be(nameof(TestLayout.ITestScope.String));

        //await client.ClickAsync(_ => area);

        AreaChangedEvent IsUpdatedView(LayoutClientState layoutClientState)
        {
            var ret = layoutClientState.GetById(TestLayout.UpdatingView);
            if (ret?.View is TextBoxControl { Data: not TestLayout.SomeString })
                return ret;

            logger.LogInformation($"Found view: {ret?.View}");
            return null;
        }

        var changedArea = await client.GetAreaAsync(IsUpdatedView);
        changedArea.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(TestLayout.NewString);


    }

}


public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}