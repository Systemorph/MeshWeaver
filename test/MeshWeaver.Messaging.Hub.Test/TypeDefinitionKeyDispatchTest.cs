using System;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>An entity keyed by its <c>Id</c> property, which may be absent.</summary>
/// <param name="Id">The key value; <c>null</c> for a keyless instance.</param>
public record KeyDispatchItem(string? Id);

/// <summary>
/// A subclass written before <see cref="TypeDefinition.GetKeyOrNull"/> existed: it overrides
/// <see cref="TypeDefinition.GetKey"/> alone, and computes a key that differs from the mapping.
/// </summary>
public record LegacyGetKeyOverride : TypeDefinition
{
    /// <summary>Initializes the subclass over <see cref="KeyDispatchItem"/>.</summary>
    /// <param name="builder">The key-function builder.</param>
    public LegacyGetKeyOverride(KeyFunctionBuilder builder)
        : base(typeof(KeyDispatchItem), nameof(KeyDispatchItem), builder)
    {
    }

    /// <inheritdoc />
    public override object GetKey(object instance) => "override:" + ((KeyDispatchItem)instance).Id;
}

/// <summary>
/// <see cref="TypeDefinition.GetKeyOrNull"/> must honour a subclass that overrides only the older
/// <see cref="TypeDefinition.GetKey"/> hook — the bulk-update path keys instances through
/// <c>GetKeyOrNull</c>, so reading the key mapping behind such an override would store the
/// instance under a different key than every other caller computes.
/// </summary>
public class TypeDefinitionKeyDispatchTest
{
    [Fact]
    public void GetKeyOrNull_DispatchesToALegacyGetKeyOverride()
    {
        var definition = new LegacyGetKeyOverride(new KeyFunctionBuilder());

        definition.GetKeyOrNull(new KeyDispatchItem("a"))
            .Should().Be("override:a", "a subclass that overrides GetKey alone keeps its key on every path");
        definition.GetKeyOrNull(new KeyDispatchItem("a"))
            .Should().Be(definition.GetKey(new KeyDispatchItem("a")));
    }

    /// <summary>Control: the base definition reads the mapping, and reports a missing key as null.</summary>
    [Fact]
    public void GetKeyOrNull_OnTheBaseDefinition_ReadsTheMapping_AndReturnsNullForAKeylessInstance()
    {
        var definition = new TypeDefinition(typeof(KeyDispatchItem), nameof(KeyDispatchItem), new KeyFunctionBuilder());

        definition.GetKeyOrNull(new KeyDispatchItem("a")).Should().Be("a");
        definition.GetKeyOrNull(new KeyDispatchItem(null)).Should().BeNull();
        Assert.Throws<InvalidOperationException>(() => definition.GetKey(new KeyDispatchItem(null)));
    }

    /// <summary>
    /// A type with no key mapping (no [Key], no Id, no SystemName) is refused BY NAME on every key
    /// read — <see cref="TypeDefinition.GetKey"/>, <see cref="TypeDefinition.GetKeyOrNull"/> and
    /// <see cref="TypeDefinition.GetKeyType"/> — never a NullReferenceException.
    /// </summary>
    [Fact]
    public void AKeylessType_IsRefusedByName_OnEveryKeyRead()
    {
        var definition = new TypeDefinition(typeof(object), nameof(Object), new KeyFunctionBuilder());

        Assert.Throws<InvalidOperationException>(() => definition.GetKey(new object()))
            .Message.Should().Contain("No key mapping");
        Assert.Throws<InvalidOperationException>(() => definition.GetKeyOrNull(new object()))
            .Message.Should().Contain("No key mapping");
        Assert.Throws<InvalidOperationException>(() => definition.GetKeyType())
            .Message.Should().Contain("No key mapping");
    }
}
