using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutExecutionAddress(object Host) : IHostedAddress;

public interface ILayout
{
    IChangeStream<EntityStore, LayoutAreaReference> Render(
        IChangeStream<WorkspaceState> changeStream,
        LayoutAreaReference reference,
        ReduceOptions options
    );
}

public sealed class LayoutPlugin : MessageHubPlugin, ILayout
{
    private readonly LayoutDefinition layoutDefinition;

    public LayoutPlugin(IMessageHub hub)
        : base(hub)
    {
        layoutDefinition = Hub
            .Configuration.GetListOfLambdas()
            .Aggregate(new LayoutDefinition(Hub), (x, y) => y.Invoke(x));
    }

    public IChangeStream<EntityStore, LayoutAreaReference> Render(
        IChangeStream<WorkspaceState> changeStream,
        LayoutAreaReference reference,
        ReduceOptions options
    ) => new LayoutManager(new(reference, Hub, changeStream, options), layoutDefinition).Render(reference);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }
}
