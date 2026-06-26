using System.ComponentModel;
using System.Linq;
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
}
