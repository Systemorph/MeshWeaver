namespace MeshWeaver.Layout;

public static class Skins
{
    public static SplitterPaneSkin SplitterPane => new();
    public static LayoutGridItemSkin LayoutGridItem => new ();
    public static BodyContentSkin BodyContent => new ();
    public static LayoutSkin Layout => new();
    public static HeaderSkin Header => new();
    public static FooterSkin Footer => new();
    public static TabSkin Tab(string label) => new (label);
}
