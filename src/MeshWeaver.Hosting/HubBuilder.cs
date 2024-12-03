using System.Collections.Immutable;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting
{
    public class HubBuilder<TBuilder>
        where TBuilder:HubBuilder<TBuilder>
    {
        public object Address { get; }
        public TBuilder This => (TBuilder)this;
        public readonly ServiceCollection Services = new();
        private List<Action<IServiceProvider>> Initializations { get; init; } = [];

        public TBuilder WithInitialization(Action<IServiceProvider> init)
        {
            Initializations.Add(init);
            return This;
        }

        private List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations { get; init; } = [];

        public TBuilder WithHubConfiguration(Func<MessageHubConfiguration, MessageHubConfiguration> config)
        {
            HubConfigurations.Add(config);
            return This;
        }

        public HubBuilder(object address)
        {
            Address = address;
            Services.AddLogging(logging => logging.AddConsole());
            Services.AddOptions();
            Services.AddScoped(sp =>
                sp.CreateMessageHub(
                    Address,
                    configuration => HubConfigurations.Aggregate(configuration, (c, cc) => cc.Invoke(c)))
            );

        }

        protected virtual IMessageHub BuildHub()
        {
            var serviceProvider = Services.CreateMeshWeaverServiceProvider();

            foreach (var initialize in Initializations)
                initialize(serviceProvider);

            return serviceProvider.GetRequiredService<IMessageHub>();
        }

    }
}
