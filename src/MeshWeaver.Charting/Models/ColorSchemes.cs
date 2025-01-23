using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models;

public record ColorSchemes
{
    // our default color scheme name. can also be an array of color hash strings.
    public object Scheme { get; init; } = Palettes.Brewer.PastelOne9;
}

