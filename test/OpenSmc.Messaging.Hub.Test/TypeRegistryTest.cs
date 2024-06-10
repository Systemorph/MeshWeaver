using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;

public class TypeRegistryTest(ITestOutputHelper output) : HubTestBase(output)
{
    record SayHelloRequest : IRequest<HelloEvent>;

    record HelloEvent;

    private record GenericRequest<T>(T Value);

    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(typeof(GenericRequest<>));

    [Fact]
    public async Task GenericTypes()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var canMap = typeRegistry.TryGetTypeName(typeof(GenericRequest<int>), out var typeName);
        canMap.Should().BeTrue();
        typeName.Should().Be("OpenSmc.Messaging.Hub.Test.TypeRegistryTest.GenericRequest`1[Int32]");

        canMap = typeRegistry.TryGetType(typeName, out var mappedType);
        canMap.Should().BeTrue();
        mappedType.Should().Be(typeof(GenericRequest<int>));
    }
}
