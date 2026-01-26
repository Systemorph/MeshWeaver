using System.Collections.Immutable;

namespace MeshWeaver.Layout.Catalog;

/// <summary>
/// A control that displays grouped catalog items with expandable sections.
/// Each group can have a key, label, emoji, order, and a list of UI controls to render.
/// </summary>
public record CatalogControl()
    : UiControl<CatalogControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The groups of items to display in the catalog.
    /// </summary>
    public ImmutableList<CatalogGroup> Groups { get; init; } = [];

    /// <summary>
    /// Whether sections can be collapsed/expanded (default true).
    /// </summary>
    public bool CollapsibleSections { get; init; } = true;

    /// <summary>
    /// Whether to show item counts in section headers (default true).
    /// </summary>
    public bool ShowCounts { get; init; } = true;

    /// <summary>
    /// Column span for extra-small screens (default 12 = full width).
    /// </summary>
    public int Xs { get; init; } = 12;

    /// <summary>
    /// Column span for small screens (default 6 = 2 columns).
    /// </summary>
    public int Sm { get; init; } = 6;

    /// <summary>
    /// Column span for medium screens (default 4 = 3 columns).
    /// </summary>
    public int Md { get; init; } = 4;

    /// <summary>
    /// Column span for large screens (default 3 = 4 columns).
    /// </summary>
    public int Lg { get; init; } = 3;

    /// <summary>
    /// Grid spacing between items (default 2).
    /// </summary>
    public int Spacing { get; init; } = 2;

    /// <summary>
    /// Gap between sections in pixels (default 16).
    /// </summary>
    public int SectionGap { get; init; } = 16;

    /// <summary>
    /// Fixed height for each card in pixels (default 140).
    /// </summary>
    public int CardHeight { get; init; } = 140;

    /// <summary>
    /// Adds a single group to the catalog.
    /// </summary>
    public CatalogControl WithGroup(CatalogGroup group) => this with { Groups = Groups.Add(group) };

    /// <summary>
    /// Adds multiple groups to the catalog.
    /// </summary>
    public CatalogControl WithGroups(IEnumerable<CatalogGroup> groups) => this with { Groups = Groups.AddRange(groups) };

    /// <summary>
    /// Adds multiple groups to the catalog.
    /// </summary>
    public CatalogControl WithGroups(ImmutableList<CatalogGroup> groups) => this with { Groups = groups };

    /// <summary>
    /// Configures the catalog using a fluent skin builder.
    /// </summary>
    public CatalogControl WithSkin(Func<CatalogSkin, CatalogSkin> configure)
    {
        var skin = configure(new CatalogSkin());
        return this with
        {
            CollapsibleSections = skin.CollapsibleSections,
            ShowCounts = skin.ShowCounts,
            Xs = skin.Xs,
            Sm = skin.Sm,
            Md = skin.Md,
            Lg = skin.Lg,
            Spacing = skin.Spacing,
            SectionGap = skin.SectionGap,
            CardHeight = skin.CardHeight
        };
    }
}

/// <summary>
/// Represents a group of items in a catalog.
/// </summary>
public record CatalogGroup
{
    /// <summary>
    /// Unique key for the group.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Display label for the group header.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Optional emoji to display before the label.
    /// </summary>
    public string? Emoji { get; init; }

    /// <summary>
    /// Sort order for the group.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether the group is initially expanded.
    /// </summary>
    public bool IsExpanded { get; init; } = true;

    /// <summary>
    /// The items in this group as UI controls.
    /// </summary>
    public ImmutableList<UiControl> Items { get; init; } = [];

    /// <summary>
    /// Total count of items (may differ from Items.Count if items are paginated).
    /// </summary>
    public int TotalCount { get; init; }
}

/// <summary>
/// Skin configuration for CatalogControl.
/// Use this for fluent configuration of the catalog appearance.
/// </summary>
public record CatalogSkin : Skin<CatalogSkin>
{
    /// <summary>
    /// Whether sections can be collapsed/expanded (default true).
    /// </summary>
    public bool CollapsibleSections { get; init; } = true;

    /// <summary>
    /// Whether to show item counts in section headers (default true).
    /// </summary>
    public bool ShowCounts { get; init; } = true;

    /// <summary>
    /// Column span for extra-small screens (default 12 = full width).
    /// </summary>
    public int Xs { get; init; } = 12;

    /// <summary>
    /// Column span for small screens (default 6 = 2 columns).
    /// </summary>
    public int Sm { get; init; } = 6;

    /// <summary>
    /// Column span for medium screens (default 4 = 3 columns).
    /// </summary>
    public int Md { get; init; } = 4;

    /// <summary>
    /// Column span for large screens (default 3 = 4 columns).
    /// </summary>
    public int Lg { get; init; } = 3;

    /// <summary>
    /// Grid spacing between items (default 2).
    /// </summary>
    public int Spacing { get; init; } = 2;

    /// <summary>
    /// Gap between sections in pixels (default 16).
    /// </summary>
    public int SectionGap { get; init; } = 16;

    /// <summary>
    /// Fixed height for each card in pixels (default 140).
    /// </summary>
    public int CardHeight { get; init; } = 140;

    public CatalogSkin WithCollapsibleSections(bool value) => this with { CollapsibleSections = value };
    public CatalogSkin WithShowCounts(bool value) => this with { ShowCounts = value };
    public CatalogSkin WithGridBreakpoints(int xs = 12, int sm = 6, int md = 4, int lg = 3) =>
        this with { Xs = xs, Sm = sm, Md = md, Lg = lg };
    public CatalogSkin WithSpacing(int value) => this with { Spacing = value };
    public CatalogSkin WithSectionGap(int value) => this with { SectionGap = value };
    public CatalogSkin WithCardHeight(int value) => this with { CardHeight = value };
}
