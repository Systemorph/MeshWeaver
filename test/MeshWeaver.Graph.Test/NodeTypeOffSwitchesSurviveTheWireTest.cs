using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A <see cref="NodeTypeDefinition"/> flag whose C# initializer is <c>true</c> must keep an explicit
/// <c>false</c> across the mesh serializer. The hub serializes with
/// <see cref="JsonIgnoreCondition.WhenWritingDefault"/>, which compares against <c>default(bool)</c>
/// = <c>false</c>, so an explicit <c>false</c> is dropped on write and the reader re-applies the
/// <c>true</c> initializer: the switch can be turned on, never off.
///
/// <para><b>Measured on a deployed portal.</b> An in-mesh NodeType whose committed <c>.json</c>
/// declares <c>"keepsHistory": false</c> read <c>keepsHistory: true</c> on its live node, with its
/// current sources adopted, so its instances kept version history the type had opted out of.
/// <c>NodeTypesThatKeepNoHistoryTest</c> did not see it: it plants the JSON straight into the store
/// and never round-trips a typed definition through the hub's options, which is what every import
/// and every stream write does.</para>
///
/// <para>The guard is reflective, so a flag added later with the same shape fails here too.
/// The negative control runs the same instrument over a record that has the trap on purpose, so a
/// round trip that could not lose the value cannot pass for a fixed one.</para>
/// </summary>
public class NodeTypeOffSwitchesSurviveTheWireTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Every case only reads the mesh's immutable serializer options and writes no mesh
    /// state, so one boot serves the whole class.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>The trap, kept on purpose: an <c>= true</c> flag with no write override.</summary>
    public sealed record TrapShaped
    {
        /// <summary>Defaults to true and is dropped from the wire when false.</summary>
        public bool Flag { get; init; } = true;
    }

    private static object WithFlagOff(object instance, PropertyInfo property)
    {
        property.SetValue(instance, false);
        return instance;
    }

    private bool RoundTrip(object instance, PropertyInfo property)
    {
        var options = Mesh.JsonSerializerOptions;
        var json = JsonSerializer.Serialize(instance, instance.GetType(), options);
        var read = JsonSerializer.Deserialize(json, instance.GetType(), options)!;
        return (bool)property.GetValue(read)!;
    }

    /// <summary>Every serialised <c>bool</c> of <see cref="NodeTypeDefinition"/> that defaults to
    /// true — the switches an author turns OFF in a NodeType's JSON.</summary>
    private static PropertyInfo[] TrueDefaultedFlags() =>
        typeof(NodeTypeDefinition)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.PropertyType == typeof(bool) && p.CanWrite
                        && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always
                        && (bool)p.GetValue(new NodeTypeDefinition())!)
            .ToArray();

    [Fact]
    public void KeepsHistory_false_survives_the_mesh_serializer()
    {
        var definition = new NodeTypeDefinition { KeepsHistory = false };
        var options = Mesh.JsonSerializerOptions;

        var json = JsonSerializer.Serialize(definition, options);
        Assert.Contains("\"keepsHistory\":false", json);
        Assert.False(JsonSerializer.Deserialize<NodeTypeDefinition>(json, options)!.KeepsHistory);

        // The shape it really travels in: a NodeType MeshNode, content polymorphic.
        var node = MeshNode.FromPath("Governance/NameCheck") with
        {
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
        };
        var back = JsonSerializer.Deserialize<MeshNode>(JsonSerializer.Serialize(node, options), options)!;
        Assert.False(back.ContentAs<NodeTypeDefinition>(options)!.KeepsHistory);
    }

    [Fact]
    public void KeepsHistory_absent_still_reads_true_and_true_serialises_as_before()
    {
        var options = Mesh.JsonSerializerOptions;
        Assert.True(JsonSerializer.Deserialize<NodeTypeDefinition>("{}", options)!.KeepsHistory);

        // A type that never declared the flag serialises exactly as before the fix — `true` is not
        // default(bool), so WhenWritingDefault always wrote it — so no stored NodeType's source
        // token moves and no import re-evaluates (and recompiles) every type in the fleet. Only a
        // type that declares false gets a new token, which is the re-import that lands the false.
        var json = JsonSerializer.Serialize(new NodeTypeDefinition(), options);
        Assert.Contains("\"keepsHistory\":true", json);
        Assert.True(JsonSerializer.Deserialize<NodeTypeDefinition>(
            JsonSerializer.Serialize(new NodeTypeDefinition { KeepsHistory = true }, options), options)!.KeepsHistory);
    }

    [Fact]
    public void Every_true_defaulted_flag_keeps_an_explicit_false()
    {
        var flags = TrueDefaultedFlags();
        Assert.NotEmpty(flags);
        foreach (var flag in flags)
            Assert.False(RoundTrip(WithFlagOff(new NodeTypeDefinition(), flag), flag),
                $"NodeTypeDefinition.{flag.Name} defaults to true; an explicit false must survive the hub serializer");
    }

    [Fact]
    public void Negative_control_the_instrument_sees_the_trap()
    {
        var flag = typeof(TrapShaped).GetProperty(nameof(TrapShaped.Flag))!;
        Assert.True(RoundTrip(WithFlagOff(new TrapShaped(), flag), flag),
            "a true-defaulted flag with no write override loses its false; if this ever passes, the round trip measures nothing");
    }
}
