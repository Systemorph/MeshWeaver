namespace MeshWeaver.Layout;

public record ArticleControl() : UiControl<ArticleControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object Name { get; init; }
    public object Collection { get; init; }
    public object Title { get; init; }
    public object Thumbnail { get; init; }

    public object Tags { get; init; }
    public object Abstract { get; init; }
    public object Authors { get; init; }
    public object Published { get; init; }
    public object LastUpdated { get; init; }

    public object Content { get; init; }
}
