using MeshWeaver.Hosting.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly:TestApplication]
namespace MeshWeaver.Hosting.Test;

public class TestApplicationAttribute : MeshNodeAttribute
{
    public const string Test = nameof(Test);
    public static readonly ApplicationAddress Address = new(Test);

    public override IMessageHub Create(IServiceProvider serviceProvider, MeshNode node)
        => CreateIf(node.AddressType == "app" && node.Id == Test, () => serviceProvider.CreateMessageHub(Address,
                conf => conf.WithHandler<Ping>((hub, delivery) =>
                {
                    hub.Post(new Pong(), o => o.ResponseFor(delivery));
                    return delivery.Processed();
                })));
}

public record Ping : IRequest<Pong>;

public record Pong;
