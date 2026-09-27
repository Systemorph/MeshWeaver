using System;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

public class TypeDefinitionMetadataTest
{
    [Fact]
    public void RepeatedFrameworkDefinitions_HaveBoundedConstructionAllocation()
    {
        var builder = new KeyFunctionBuilder();
        for (var i = 0; i < 32; i++)
            _ = new TypeDefinition(typeof(string), "String", builder);

        var held = new TypeDefinition[1000];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < held.Length; i++)
            held[i] = new TypeDefinition(typeof(string), "String", builder);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The original per-definition reflection/Wordify/lazy-description graph costs 1,032,000
        // bytes for this workload. Keep a fixed budget that rejects that measured regression.
        allocated.Should().BeLessThan(600_000);
        GC.KeepAlive(held);
    }

    [Fact]
    public void CachedDeclaration_StillResolvesResourcePropertiesForEveryDefinition()
    {
        var builder = new KeyFunctionBuilder();
        try
        {
            DisplayResources.Value = "first";
            var first = new TypeDefinition(typeof(ResourceNamedContent), "First", builder);
            DisplayResources.Value = "second";
            var second = new TypeDefinition(typeof(ResourceNamedContent), "Second", builder);

            first.DisplayName.Should().Be("first name");
            first.GroupName.Should().Be("first group");
            second.DisplayName.Should().Be("second name");
            second.GroupName.Should().Be("second group");
        }
        finally
        {
            DisplayResources.Value = "default";
        }
    }

    [Fact]
    public void ExplicitDescriptionOverride_DoesNotChangeOtherDefinitions()
    {
        var builder = new KeyFunctionBuilder();
        var first = new TypeDefinition(typeof(KeyFunctionBuilder), "First", builder);
        var original = first.Description;
        original.Should().Contain("Builds");

        var overridden = first with { Description = "Explicit description" };
        var second = new TypeDefinition(typeof(KeyFunctionBuilder), "Second", builder);

        overridden.Description.Should().Be("Explicit description");
        first.Description.Should().Be(original);
        second.Description.Should().Be(original);
    }

    [Fact]
    public void SharedDisplayMetadata_DoesNotShareKeyFunctionBuilders()
    {
        var firstBuilder = new KeyFunctionBuilder();
        firstBuilder.WithKeyFunction(type => type == typeof(ResourceNamedContent)
            ? new KeyFunction(_ => "first key", typeof(string)) : null);
        var secondBuilder = new KeyFunctionBuilder();
        secondBuilder.WithKeyFunction(type => type == typeof(ResourceNamedContent)
            ? new KeyFunction(_ => "second key", typeof(string)) : null);
        var first = new TypeDefinition(typeof(ResourceNamedContent), "First", firstBuilder);
        var second = new TypeDefinition(typeof(ResourceNamedContent), "Second", secondBuilder);
        var content = new ResourceNamedContent();

        first.GetKey(content).Should().Be("first key");
        second.GetKey(content).Should().Be("second key");
    }

    [Display(Name = nameof(DisplayResources.Name), GroupName = nameof(DisplayResources.Group),
        ResourceType = typeof(DisplayResources))]
    private sealed class ResourceNamedContent;

    public static class DisplayResources
    {
        public static string Value { get; set; } = "default";
        public static string Name => Value + " name";
        public static string Group => Value + " group";
    }
}
