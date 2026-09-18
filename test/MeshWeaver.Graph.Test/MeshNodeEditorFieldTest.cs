using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="MeshNodeEditorField.FromType"/> reflection: <c>[Browsable(false)]</c> is skipped,
/// bool → checkbox, enum (incl. <c>Nullable&lt;enum&gt;</c>) → dropdown with the member names as
/// options, everything else → text. The enum dropdown is what the platform Update Policy admin tab
/// relies on (a non-enum kind would silently degrade it to a free-text field).
/// </summary>
public class MeshNodeEditorFieldTest
{
    private enum Color { Red, Green, Blue }

    private record Sample
    {
        [Description("Hue")] public Color Hue { get; init; }
        public Color? MaybeHue { get; init; }
        public bool Flag { get; init; }
        public string? Text { get; init; }
        [Browsable(false)] public string? Hidden { get; init; }
    }

    [Fact]
    public void FromType_MapsKinds_EnumOptions_AndSkipsBrowsableFalse()
    {
        var fields = MeshNodeEditorField.FromType(typeof(Sample));

        Assert.DoesNotContain(fields, f => f.Key == "hidden");

        var hue = fields.Single(f => f.Key == "hue");
        Assert.Equal(MeshNodeEditorFieldKind.Enum, hue.Kind);
        Assert.Equal("Hue", hue.Label);                              // [Description] wins over the property name
        Assert.Equal(new[] { "Red", "Green", "Blue" }, hue.Options);

        // Nullable<enum> is still an Enum field.
        Assert.Equal(MeshNodeEditorFieldKind.Enum, fields.Single(f => f.Key == "maybeHue").Kind);

        Assert.Equal(MeshNodeEditorFieldKind.Bool, fields.Single(f => f.Key == "flag").Kind);
        Assert.Equal(MeshNodeEditorFieldKind.Text, fields.Single(f => f.Key == "text").Kind);
        Assert.Empty(fields.Single(f => f.Key == "text").Options);   // only enums carry options
    }

    /// <summary>
    /// A content record whose CLR names and JSON names DIVERGE — the shape
    /// <c>UpdatePolicyContent</c> took on when <c>Policy</c> became <c>DeclaredPolicy</c> +
    /// <c>[JsonPropertyName("policy")]</c> so an absent declaration could fail closed (#3607).
    /// </summary>
    private record Renamed
    {
        [JsonPropertyName("policy")] public Color? DeclaredPolicy { get; init; }

        /// <summary>A read-side convenience the record does not persist.</summary>
        [JsonIgnore] public Color Effective => DeclaredPolicy ?? Color.Red;

        /// <summary>The OPPOSITE attribute: "always write this, even at its CLR default".</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool Loud { get; init; } = true;

        public string? Plain { get; init; }
    }

    /// <summary>
    /// 🚨 <b><see cref="MeshNodeEditorField.Key"/> is the JSON name, never the CLR name (#3542).</b>
    /// <c>MeshNodeContentEditorView</c> uses it verbatim as a key into the node content's JSON
    /// object, on BOTH sides — <c>obj[f.Key]</c> to read and <c>obj[f.Key]</c> to write. A key
    /// derived from the property name binds the control to a field that does not exist the moment a
    /// property is renamed behind a <c>[JsonPropertyName]</c>: the read renders unset over a value
    /// that IS there, and the write lands under a key the record ignores. Both halves are silent,
    /// and the junk key echoes back so the UI reads as though the edit had applied.
    /// </summary>
    [Fact]
    public void FromType_KeysFieldsByTheirJsonName_NotTheirClrName()
    {
        var fields = MeshNodeEditorField.FromType(typeof(Renamed));

        Assert.Contains(fields, f => f.Key == "policy");
        Assert.DoesNotContain(fields, f => f.Key == "declaredPolicy");

        // 🚨 The DENOMINATOR: every declared key must be one the serializer actually reads and
        // writes. Anything else binds a control to nothing, which is the whole defect.
        var wire = JsonSerializer.SerializeToNode(
                new Renamed { DeclaredPolicy = Color.Green, Plain = "x" },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!
            .AsObject()
            .Select(p => p.Key)
            .ToHashSet();

        Assert.All(fields, f => Assert.Contains(f.Key, wire));
    }

    /// <summary>
    /// 🚨 <c>[JsonIgnore]</c> is not editable — such a property is one the record does NOT persist,
    /// so a control over it could only ever write a key the type drops on read. Only the default
    /// <see cref="JsonIgnoreCondition.Always"/> form is skipped: <c>Condition = Never</c> means the
    /// opposite ("always write this, even at its default") and stays editable, which is what
    /// <c>NotificationSettings</c> and <c>GitHubSyncConfig</c> rely on for every default-true bool.
    /// </summary>
    [Fact]
    public void FromType_SkipsJsonIgnore_ButKeepsTheNeverCondition()
    {
        var keys = MeshNodeEditorField.FromType(typeof(Renamed)).Select(f => f.Key).ToArray();

        Assert.DoesNotContain("effective", keys);
        Assert.Contains("loud", keys);
        Assert.Contains("plain", keys);
    }
}
