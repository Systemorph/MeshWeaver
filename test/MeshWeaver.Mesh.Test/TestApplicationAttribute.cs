using MeshWeaver.Application;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;

[assembly:TestApplication]
namespace MeshWeaver.Mesh.Test;

internal class TestApplicationAttribute : MeshNodeAttribute
{
    public const string Test = nameof(Test);
    public static readonly ApplicationAddress Address = new(Test);

    public override IMessageHub Create(IServiceProvider serviceProvider, MeshNode node)
        => CreateIf(node.Matches(Address), () => serviceProvider.CreateMessageHub(Address,
                conf => conf.WithHandler<Ping>((hub, delivery) =>
                {
                    hub.Post(new Pong(), o => o.ResponseFor(delivery));
                    return delivery.Processed();
                })));
}

public record Ping : IRequest<Pong>;

public record Pong;
