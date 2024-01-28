using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application;
using OpenSmc.Application.Scope;
using OpenSmc.Fixture;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.LayoutClient;
using OpenSmc.Messaging;
using OpenSmc.Portal;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test
{
    public class LayoutStackControlTest : TestBase
    {
        public LayoutStackControlTest(ITestOutputHelper output)
            : base(output)
        {
            LayoutAddress = new LayoutAddress("app");
            ControlAddress = new UiControlAddress(TestAreas.Main, LayoutAddress);
            Services.AddSingleton<IMessageHub>(serviceProvider =>
                                                   serviceProvider.CreateMessageHub(LayoutAddress,
                                                                                    conf => conf
                                                                                            .AddLayout(def =>
                                                                                                           def.WithInitialState(Controls.Stack()
                                                                                                                                        .WithId(MainStackId)
                                                                                                                                        .WithClickAction(context =>
                                                                                                                                        {
                                                                                                                                            context.Hub.Post(MessageOnClick);
                                                                                                                                            return Task.CompletedTask;
                                                                                                                                        })))
                                                                                            .WithServices(services =>
                                                                                                              services.Configure<ApplicationAddressOptions>(options => options.Address = new ApplicationAddress("1", "test")))
                                                                                   ));

            Services.AddSingleton(serviceProvider =>
                                      serviceProvider.CreateMessageHub(new ClientAddress(),
                                                                       conf => conf.AddLayoutClient(new RefreshRequest(), LayoutAddress)));
        }
        //.WithClick<string>(context => )
        [Inject] protected IMessageHub Layout;
        [Inject] protected IMessageHub<ClientAddress> Client;

        private const string MainStackId = nameof(MainStackId);

        protected LayoutAddress LayoutAddress { get; set; }
        protected ApplicationAddress ApplicationAddress { get; set; }
        protected object ControlAddress { get; set; }
        private object MessageOnClick;

        public override void Initialize()
        {
            base.Initialize();
        }

        private IMessageDelivery SendToHost(IMessageDelivery request)
        {
            return Layout!.DeliverMessage(request);
        }
        private IMessageDelivery SendToClient(IMessageDelivery request)
        {
            return Client!.DeliverMessage(request);
        }

        [Fact]
        public async Task LayoutStackUpdateTest()
        {
            var stack = await Client.GetAreaAsync(state => state.GetAreasByControlId(MainStackId).FirstOrDefault());
            stack.View.Should().BeOfType<LayoutStackControl>().Which.Areas.Should().BeEmpty();
            MessageOnClick = new SetAreaRequest(new SetAreaOptions(TestAreas.NewArea), Controls.TextBox("Hello").WithId("HelloId"));
            await Client.ClickAsync(state => stack);

            await Client.GetAreaAsync(state => state.GetAreasByControlId("HelloId").FirstOrDefault());
            stack = await Client.GetAreaAsync(state => state.GetAreasByControlId(MainStackId).FirstOrDefault());
            stack.View.Should().BeOfType<LayoutStackControl>().Which.Areas.Should().HaveCount(1);

        }

        public override async Task DisposeAsync()
        {
            await Layout.DisposeAsync();
            await Client.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    public record ClientAddress();

    public static class TestAreas
    {
        public const string Main = nameof(Main);
        public const string ModelStack = nameof(ModelStack);
        public const string NewArea = nameof(NewArea);
    }
}
