using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A package re-install writes a NodeType node only when <see cref="PackageInstaller.IsUnchanged"/>
/// says the file differs from the stored node. That gate must see EVERY member the repo owns.
///
/// <para><b>Measured on a deployed portal (MeshWeaver.Plugins#2786).</b> <c>Governance/NameCheck</c>
/// declares <c>"keepsHistory": false</c> in its committed file; the stored definition read
/// <c>keepsHistory: true</c> at version 143, five minor releases of the package later. The gate
/// compared <c>Configuration</c>, <c>Sources</c> and <c>InstanceLocations</c> only, so a file that
/// changed any other authored member was "unchanged" and was never written — the type kept the value
/// it was first installed with. The serializer fix that lets an explicit <c>false</c> survive a write
/// could not help: no write was ever issued.</para>
///
/// <para>The guard is reflective over the record, so a member added later is covered the day it is
/// added. The two controls keep the instrument honest: a compile-bookkeeping difference must still
/// read as unchanged (or every re-install rewrites and recompiles every type), and an identical
/// definition must read as unchanged.</para>
/// </summary>
public sealed class AReinstallLandsEveryAuthoredNodeTypeMemberTest
{
    /// <summary>The hub's write policy: defaults are dropped unless a member overrides it.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly NodeTypeDefinition Baseline = new()
    {
        Description = "a type",
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
    };

    private static MeshNode Node(NodeTypeDefinition definition) =>
        new("NameCheck", "Governance") { NodeType = MeshNode.NodeTypePath, Name = "Name Check", Content = definition };

    /// <summary>The serialised members of the record: what a file or a wire payload can carry.</summary>
    private static IEnumerable<PropertyInfo> SerializedMembers() =>
        typeof(NodeTypeDefinition)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always
                        && p.GetCustomAttribute<JsonExtensionDataAttribute>() is null
                        && p.SetMethod is not null);

    private static bool IsMeshOwned(PropertyInfo property) =>
        NodeTypeOperationalContent.MemberNames.Contains(property.Name)
        || NodeTypeOperationalContent.StrippedButNotPreserved.Contains(property.Name);

    /// <summary>A value of the member's type that differs from the baseline's, or null when this
    /// instrument does not know how to make one — which the test then reports rather than skips.</summary>
    private static object? Different(PropertyInfo property, object? current)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type == typeof(string)) return (string?)current + "-changed";
        if (type == typeof(long)) return (long)(current ?? 0L) + 1;
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        if (type == typeof(List<string>)) return new List<string> { "changed" };
        if (type == typeof(IReadOnlyList<string>)) return new[] { "changed" };
        if (type == typeof(ImmutableList<string>)) return ImmutableList.Create("changed");
        if (type == typeof(Dictionary<string, object>)) return new Dictionary<string, object> { ["changed"] = "yes" };
        if (type == typeof(IReadOnlyDictionary<string, long>)) return new Dictionary<string, long> { ["changed"] = 1 };
        if (type == typeof(ImmutableSortedDictionary<string, string>))
            return ImmutableSortedDictionary<string, string>.Empty.Add("changed", "yes");
        if (type == typeof(List<MeshWeaver.ContentCollections.ContentCollectionConfig>))
            return new List<MeshWeaver.ContentCollections.ContentCollectionConfig>
            {
                new() { Name = "changed", SourceType = "FileSystem" },
            };
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current ?? Activator.CreateInstance(type)));
        return null;
    }

    private static NodeTypeDefinition With(PropertyInfo property, object value)
    {
        var copy = Baseline with { };
        property.SetValue(copy, value);
        return copy;
    }

    [Fact]
    public void AFileThatChangesAnyAuthoredMember_IsAChange()
    {
        var authored = SerializedMembers().Where(p => !IsMeshOwned(p)).ToArray();
        Assert.Contains(authored, p => p.Name == nameof(NodeTypeDefinition.KeepsHistory));

        var unseen = new List<string>();
        var unprobed = new List<string>();
        foreach (var property in authored)
        {
            if (Different(property, property.GetValue(Baseline)) is not { } value)
            {
                unprobed.Add($"{property.Name} ({property.PropertyType.Name})");
                continue;
            }
            if (PackageInstaller.IsUnchanged(Node(Baseline), Node(With(property, value)), Options))
                unseen.Add(property.Name);
        }

        Assert.True(unprobed.Count == 0,
            "this instrument cannot make a differing value for: " + string.Join(", ", unprobed)
            + " — teach Different() the type; an unprobed member is an unchecked one");
        Assert.True(unseen.Count == 0,
            "a re-install whose file changes one of these authored members reads as UNCHANGED, so the "
            + "stored NodeType keeps its old value forever: " + string.Join(", ", unseen));
    }

    [Fact]
    public void TheMeasuredCase_KeepsHistoryTurnedOffInTheFile_IsAChange()
    {
        var stored = Node(Baseline);                                    // reads keepsHistory: true
        var file = Node(Baseline with { KeepsHistory = false });

        Assert.False(PackageInstaller.IsUnchanged(stored, file, Options));
    }

    [Fact]
    public void Control_ADifferenceInCompileBookkeepingOnly_IsUnchanged()
    {
        var owned = SerializedMembers().Where(IsMeshOwned).ToArray();
        Assert.NotEmpty(owned);

        var stored = Baseline with { };
        var unprobed = new List<string>();
        foreach (var property in owned)
        {
            if (Different(property, property.GetValue(Baseline)) is { } value)
                property.SetValue(stored, value);
            else
                unprobed.Add($"{property.Name} ({property.PropertyType.Name})");
        }

        // The members this instrument cannot fill are named, and the control still has to carry the
        // ones every live type carries — otherwise it could pass having compared two baselines.
        stored = stored with { CompilationStatus = CompilationStatus.Ok, BuildProvenance = BuildProvenance.AdoptedVerified };
        Assert.True(unprobed.Count <= 2,
            "the control fills too few mesh-owned members to mean anything; unfilled: " + string.Join(", ", unprobed));

        Assert.True(PackageInstaller.IsUnchanged(Node(stored), Node(Baseline), Options),
            "a stored NodeType differs from its file by the mesh's own compile state on every re-install; "
            + "reading that as a change rewrites and recompiles every type on every sync");
    }

    [Fact]
    public void Control_TheSameDefinition_IsUnchanged() =>
        Assert.True(PackageInstaller.IsUnchanged(Node(Baseline), Node(Baseline with { }), Options));
}
