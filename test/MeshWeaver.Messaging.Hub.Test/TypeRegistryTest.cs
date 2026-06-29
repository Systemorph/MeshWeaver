using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

public class TypeRegistryTest(ITestOutputHelper output) : HubTestBase(output)
{
    record SayHelloRequest : IRequest<HelloEvent>;

    record HelloEvent;

    private record GenericRequest<T>(T Value);

    // A type deliberately NOT registered on any hub — used to prove the resolver warns when a hub
    // serializes a type its TypeRegistry doesn't know (the "serialized from the wrong place" defect).
    private record UnregisteredPayload(string Value);

    // A stand-in for a security content type, used to pin the legacy full-name-alias ordering contract
    // (register full name FIRST, short name LAST) that WithGraphTypes / AddAITypes / AddMeshTypes rely on.
    private record AliasedContent(bool PublicRead);

    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(typeof(GenericRequest<>), typeof(List<>));

    [Fact]
    public void GenericTypes()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var canMap = typeRegistry.TryGetCollectionName(typeof(GenericRequest<int>), out var typeName);
        canMap.Should().BeTrue();
        typeName.Should().Be("MeshWeaver.Messaging.Hub.Test.TypeRegistryTest.GenericRequest`1[Int32]");

        canMap = typeRegistry.TryGetType(typeName!, out var mappedType);
        canMap.Should().BeTrue();
        mappedType!.Type.Should().Be(typeof(GenericRequest<int>));
    }

    [Fact]
    public void GenericTypesWithNullableArguments()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();

        // Test List<int?>
        var canMap = typeRegistry.TryGetCollectionName(typeof(List<int?>), out var typeName);
        canMap.Should().BeTrue();
        typeName.Should().Be("System.Collections.Generic.List`1[Int32?]");

        canMap = typeRegistry.TryGetType(typeName!, out var mappedType);
        canMap.Should().BeTrue();
        mappedType!.Type.Should().Be(typeof(List<int?>));

        // Test GenericRequest<int?>
        canMap = typeRegistry.TryGetCollectionName(typeof(GenericRequest<int?>), out typeName);
        canMap.Should().BeTrue();
        typeName.Should().Be("MeshWeaver.Messaging.Hub.Test.TypeRegistryTest.GenericRequest`1[Int32?]");

        canMap = typeRegistry.TryGetType(typeName!, out mappedType);
        canMap.Should().BeTrue();
        mappedType!.Type.Should().Be(typeof(GenericRequest<int?>));
    }

    /// <summary>
    /// Serializing a type that is NOT in the serializing hub's registry stamps a full-name $type and
    /// MUST warn — that warning is how we find a node "serialized from the wrong place" (the
    /// _Provider/_Policy storm). The warning names the type and the publishing hub, and is deduped.
    /// </summary>
    [Fact]
    public void SerializingUnregisteredType_WarnsAndNamesThePublisher()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        // Sanity: the payload type is genuinely unregistered on this hub.
        typeRegistry.TryGetCollectionName(typeof(UnregisteredPayload), out _).Should().BeFalse();

        var captured = new ConcurrentQueue<string>();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectPolymorphicConverter(typeRegistry));
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(
            typeRegistry, owner: "mesh/the-publishing-hub", logger: new CapturingLogger(captured));

        // Serialize through the open `object` door so the polymorphic path (and the resolver) runs.
        var json = JsonSerializer.Serialize((object)new UnregisteredPayload("hi"), typeof(object), options);

        // It WAS written with the namespace-qualified full name (the latent cross-hub-read defect).
        // FormatType normalizes the nested-type '+' separator to '.', matching what gets persisted.
        var persistedDiscriminator = typeof(UnregisteredPayload).FullName!.Replace('+', '.');
        json.Should().Contain(persistedDiscriminator);
        // ...and the resolver surfaced exactly one warning naming the type + the publishing hub.
        var warnings = captured.Where(m =>
            m.Contains("Unregistered type") &&
            m.Contains(typeof(UnregisteredPayload).FullName!) &&
            m.Contains("mesh/the-publishing-hub")).ToList();
        warnings.Should().ContainSingle("the unregistered-type warning fires once per type and identifies the hub");
    }

    /// <summary>
    /// The legacy full-name alias contract: registering the full name FIRST and the short
    /// <c>nameof</c> LAST makes BOTH discriminators resolve on read while the hub keeps WRITING the
    /// short name (nameByType is last-write-wins). This is exactly what WithGraphTypes / AddAITypes /
    /// AddMeshTypes now do for the security content types so legacy full-name nodes deserialize.
    /// </summary>
    [Fact]
    public void FullNameAlias_ResolvesBothDiscriminators_ButWritesShortName()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();

        typeRegistry.WithType(typeof(AliasedContent), typeof(AliasedContent).FullName!); // full name FIRST
        typeRegistry.WithType(typeof(AliasedContent), nameof(AliasedContent));           // short name LAST

        // Read: a legacy full-name $type AND the short name both resolve to the same type.
        typeRegistry.TryGetType(typeof(AliasedContent).FullName!, out var byFull).Should().BeTrue();
        byFull!.Type.Should().Be(typeof(AliasedContent));
        typeRegistry.TryGetType(nameof(AliasedContent), out var byShort).Should().BeTrue();
        byShort!.Type.Should().Be(typeof(AliasedContent));

        // Write: the hub emits the SHORT name (so it never re-introduces full-name nodes).
        typeRegistry.TryGetCollectionName(typeof(AliasedContent), out var writeName).Should().BeTrue();
        writeName.Should().Be(nameof(AliasedContent));
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Enqueue(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
