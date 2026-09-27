using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>Unrelated registered types must not allocate a predicate per metadata resolution.</summary>
public class PolymorphicResolverAllocationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Probe(string Value);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(DeclaredSubtype), "declared")]
    private abstract record PolymorphicBase;

    private record DeclaredSubtype(string Value) : PolymorphicBase;

    private record RegisteredSubtype(string Value) : PolymorphicBase;

    [Fact]
    public void AttributeDiscriminatorWinsOverRegistryAlias_AndOtherSubtypeStillRoundTrips()
    {
        var registry = GetHost().ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.WithType(typeof(DeclaredSubtype), "registry-alias");
        registry.WithType(typeof(RegisteredSubtype), "registered");
        var options = new JsonSerializerOptions { TypeInfoResolver = new PolymorphicTypeInfoResolver(registry) };
        var info = options.GetTypeInfo(typeof(PolymorphicBase));
        info.PolymorphismOptions!.DerivedTypes.Should().HaveCount(2);
        foreach (PolymorphicBase value in new PolymorphicBase[]
                 { new DeclaredSubtype("attribute"), new RegisteredSubtype("registry") })
        {
            var json = JsonSerializer.Serialize(value, options);
            json.Should().Contain(value is DeclaredSubtype ? "\"$type\":\"declared\"" : "\"$type\":\"registered\"");
            JsonSerializer.Deserialize<PolymorphicBase>(json, options).Should().Be(value);
        }
    }

    [Fact]
    public void UnrelatedRegistryEntries_DoNotAllocateAClosurePerCandidate()
    {
        var registry = GetHost().ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.WithType(typeof(Probe), nameof(Probe));
        var resolver = new PolymorphicTypeInfoResolver(registry);
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        resolver.GetTypeInfo(typeof(Probe), options);
        var small = ResolutionBytes(resolver, options) - TraversalBytes(registry);

        const int candidates = 512;
        var module = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("UnrelatedResolutionCandidates"), AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("Candidates");
        for (var i = 0; i < candidates; i++)
        {
            var type = module.DefineType($"Unrelated{i}", TypeAttributes.Public | TypeAttributes.Class)
                .CreateType()!;
            registry.WithType(type, type.Name);
        }

        resolver.GetTypeInfo(typeof(Probe), options);
        var large = ResolutionBytes(resolver, options) - TraversalBytes(registry);
        var added = large - small;
        Output.WriteLine($"Extra resolution allocation beyond registry traversal: {added:N0} bytes "
            + $"for {candidates} unrelated types (small={small:N0}, large={large:N0}).");
        added.Should().BeLessThan(1024L,
            "unrelated candidates need an assignability check, not a captured Any predicate "
            + "and boxed list enumerator for each registry entry");
        resolver.GetTypeInfo(typeof(Probe), options).PolymorphismOptions!.DerivedTypes
            .Should().ContainSingle().Which.DerivedType.Should().Be(typeof(Probe));
    }

    private static long ResolutionBytes(PolymorphicTypeInfoResolver resolver, JsonSerializerOptions options)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            resolver.GetTypeInfo(typeof(Probe), options);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
    }

    // Registry enumeration itself materialises a DistinctBy set. Subtract that same real traversal
    // so this reading isolates the resolver's work rather than imposing a new registry contract.
    private static long TraversalBytes(ITypeRegistry registry)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            foreach (var entry in registry.Types)
                GC.KeepAlive(entry.Value);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
    }
}
