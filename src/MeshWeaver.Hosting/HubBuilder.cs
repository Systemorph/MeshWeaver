using System.Collections.Immutable;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting
{
    /// <summary>
    /// Base builder for assembling a message hub's service collection, configuration, and
    /// initialization steps. Uses the curiously-recurring template pattern so fluent methods
    /// return the concrete derived builder type.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete builder type deriving from this base.</typeparam>
    public class HubBuilder<TBuilder>
        where TBuilder:HubBuilder<TBuilder>
    {
        /// <summary>The mesh address of the hub being built.</summary>
        public Address Address { get; }
        /// <summary>This instance typed as the concrete derived builder, for fluent chaining.</summary>
        public TBuilder This => (TBuilder)this;
        /// <summary>The service collection backing the hub being built.</summary>
        public readonly ServiceCollection Services = new();
        private List<Action<IServiceProvider>> Initializations { get; init; } = [];

        /// <summary>
        /// Registers a callback run against the built service provider before the hub is resolved.
        /// </summary>
        /// <param name="init">The initialization action to run.</param>
        /// <returns>This builder, for chaining.</returns>
        public TBuilder WithInitialization(Action<IServiceProvider> init)
        {
            Initializations.Add(init);
            return This;
        }

        private List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations { get; init; } = [];

        /// <summary>
        /// Adds a transformation applied to the hub's <see cref="MessageHubConfiguration"/> at build time.
        /// </summary>
        /// <param name="config">The configuration transform to apply.</param>
        /// <returns>This builder, for chaining.</returns>
        public TBuilder ConfigureHub(Func<MessageHubConfiguration, MessageHubConfiguration> config)
        {
            HubConfigurations.Add(config);
            return This;
        }

        /// <summary>
        /// Initializes the builder for a hub at the given address, wiring default logging, options,
        /// and the scoped message hub registration.
        /// </summary>
        /// <param name="address">The mesh address of the hub to build.</param>
        public HubBuilder(Address address)
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

        /// <summary>
        /// Builds the service provider, runs all registered initializations, and resolves the hub.
        /// </summary>
        /// <returns>The constructed <see cref="IMessageHub"/>.</returns>
        protected virtual IMessageHub BuildHub()
        {
            var serviceProvider = Services.CreateMeshWeaverServiceProvider();

            foreach (var initialize in Initializations)
                initialize(serviceProvider);

            return serviceProvider.GetRequiredService<IMessageHub>();
        }

    }
}
