namespace OpenSmc.Application.Layout.DataBinding;

public record Binding(string Path)
{
    // TODO V10: Fix $type (2023/08/28, Alexander Yolokhov)
    // [JsonProperty("$type")]
    // public string Type => "Binding";
}