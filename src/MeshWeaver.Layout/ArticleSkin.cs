namespace MeshWeaver.Layout;

public record ArticleSkin : Skin<ArticleSkin>
{
    public object Name { get; init; }
    public object Collection { get; init; }
    public object Title { get; init; }

    public object Tags { get; init; }
    public object Abstract { get; init; }
    public object Authors { get; init; }
    public object Published { get; set; }
}
