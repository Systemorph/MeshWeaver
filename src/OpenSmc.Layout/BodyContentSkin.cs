using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record BodyContentSkin : Skin<BodyContentSkin>
{
    public bool Collapsible { get; init; } = true;

    public int? Width { get; init; } = 250;
    internal object View { get; init; }

}
