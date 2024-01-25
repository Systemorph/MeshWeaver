using OpenSmc.Layout;
using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Application.Layout.Composition;

public record LayoutDefinition<THub>(THub Hub) : LayoutDefinition
{
    public LayoutDefinition<THub> WithInitialState(LayoutStackControl initialState) => this with { InitialState = initialState };
    public LayoutDefinition<THub> WithViewGenerator(Func<IMessageDelivery<RefreshRequest>, object> viewGenerator) => this with { ViewGenerator = viewGenerator };
    public LayoutDefinition<THub> WithView(Func<string, bool> filter, Func<string, SetAreaOptions, object> viewGenerator) => this with { ViewGeneratorsByPath = ViewGeneratorsByPath.Add(new(filter, viewGenerator)) };
}


internal record ViewGeneratorByPath(Func<string, bool> Filter, Func<string, SetAreaOptions, object> ViewGenerator);

public record LayoutDefinition : MessageHubModuleConfiguration
{
    internal LayoutStackControl InitialState { get; init; }
    internal Func<IMessageDelivery<RefreshRequest>, object> ViewGenerator { get; init; }
    internal ImmutableList<ViewGeneratorByPath> ViewGeneratorsByPath { get; init; } = ImmutableList<ViewGeneratorByPath>.Empty;
}