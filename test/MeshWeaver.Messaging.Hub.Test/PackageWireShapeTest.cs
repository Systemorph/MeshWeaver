using System.Text.Json;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the transport wire shape of <see cref="IMessageDelivery.Package(JsonSerializerOptions)"/>.
/// A delivery that carries no captured serializer options — the shape
/// <c>MessageDeliveryConverter</c>'s fallback produces when a delivery crosses a serialization
/// boundary — and is then re-typed via <see cref="IMessageDelivery.WithMessage"/> (the
/// sync-stream response path) must package with the TRANSPORT hub's options: camelCase
/// properties and <see cref="RawJson"/> inlined. Packaging with the runtime defaults instead
/// (PascalCase, RawJson as a <c>{"Content": …}</c> record) put a frame on the gRPC/SignalR wire
/// that no client contract recognizes — the gRPC-web live takeover silently rendered nothing.
/// </summary>
public class PackageWireShapeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record WireEvent(RawJson Change, string ChangeType);

    [Fact]
    public void RetypedOptionlessDeliveryPackagesWithTransportOptions()
    {
        var host = GetHost();
        var hubOptions = host.JsonSerializerOptions;

        // The boundary shape: constructed through the parameterless (deserialization) ctor,
        // so NO serializer options are captured on the envelope.
        IMessageDelivery boundary = new MessageDelivery<RawJson>
        {
            Message = new RawJson("{}"),
            Sender = new Address("test/sender"),
            Target = new Address("test/target"),
        };

        // Re-typed by a handler, then packaged at the transport with the hub's options.
        var retyped = boundary.WithMessage(new WireEvent(new RawJson("""{"areas":{}}"""), "Full"));
        var packaged = retyped.Package(hubOptions);

        packaged.Message.Should().BeOfType<RawJson>();
        var json = ((RawJson)packaged.Message).Content;

        // camelCase + inlined RawJson — the documented client contract (grpcSource.ts / mesh.ts).
        json.Should().Contain("\"change\":{\"areas\":{}}");
        json.Should().Contain("\"changeType\":\"Full\"");
        json.Should().NotContain("\"Content\"", "record-shaped RawJson wrapping is the wire defect");
    }

    [Fact]
    public void RetypedDeliveryKeepsItsCapturedOptions()
    {
        var host = GetHost();
        var hubOptions = host.JsonSerializerOptions;

        // A delivery created with captured options (the hub.Post path) stays wire-correct through
        // a WithMessage re-type even when Package is called WITHOUT a fallback.
        IMessageDelivery posted = new MessageDelivery<RawJson>(
            new Address("test/sender"), new Address("test/target"), new RawJson("{}"), hubOptions);
        var retyped = posted.WithMessage(new WireEvent(new RawJson("""{"areas":{}}"""), "Full"));
        var packaged = retyped.Package();

        packaged.Message.Should().BeOfType<RawJson>();
        var json = ((RawJson)packaged.Message).Content;
        json.Should().Contain("\"change\":{\"areas\":{}}");
        json.Should().Contain("\"changeType\":\"Full\"");
    }
}
