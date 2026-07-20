using System.ComponentModel;

namespace MeshWeaver.Graph;

/// <summary>How deep the home catalog lists content.</summary>
public enum HomeCatalogScope
{
    /// <summary>Only the TOP-LEVEL entries — the partition roots (spaces, courses, plugins) the viewer
    /// can read plus their own top-level home items. A shallow, first-level index (the default).</summary>
    FirstLevel,

    /// <summary>Everything the viewer can read, at every depth (the full tree).</summary>
    Subtree,
}

/// <summary>How the home catalog renders its items.</summary>
public enum HomeCatalogRender
{
    /// <summary>One flat list of items — no per-type sections (the default).</summary>
    Flat,

    /// <summary>Grouped into collapsible per-type sections with counts.</summary>
    Grouped,
}

/// <summary>The default sort order of the home catalog (the user can still switch it).</summary>
public enum HomeCatalogSort
{
    /// <summary>Most-recently-opened first — the user's own access recency (the default).</summary>
    LastAccessed,

    /// <summary>Most-recently-edited first.</summary>
    LastModified,

    /// <summary>Alphabetical, by name.</summary>
    Alphabetical,
}

/// <summary>
/// The DATA-DRIVEN display config for the user home's catalog region — the platform node an admin edits
/// (in-platform, no code change or image roll) to change how EVERY user's home lists content. Read
/// reactively from the well-known platform node (<see cref="MeshWeaver.Graph.Configuration.HomeConfigNodeType.ConfigPath"/>);
/// when the node is absent (or a field is unset) the shipped defaults apply — <b>FirstLevel + Flat +
/// LastAccessed</b> — so the home behaves identically with or without the node. Kept deliberately small:
/// this is "the home's display settings live in a node an admin can edit", not a templating engine.
/// </summary>
public record HomeConfig
{
    /// <summary>Depth of the home listing: FirstLevel (top-level entries only) or Subtree (the full tree).</summary>
    [Description("How deep the home lists content. FirstLevel shows only top-level entries (the spaces, courses and plugins you can see, plus your own top-level home items). Subtree shows everything you can read.")]
    public HomeCatalogScope Scope { get; init; } = HomeCatalogScope.FirstLevel;

    /// <summary>How items are rendered: Flat (one list) or Grouped (per-type sections).</summary>
    [Description("How the home renders items. Flat is one list; Grouped shows collapsible per-type sections.")]
    public HomeCatalogRender Render { get; init; } = HomeCatalogRender.Flat;

    /// <summary>The default ordering (the user can still change it via the Sort-by control).</summary>
    [Description("The default ordering. Last accessed shows your recently-opened items first; Last modified shows recent edits; Alphabetical sorts by name.")]
    public HomeCatalogSort DefaultSort { get; init; } = HomeCatalogSort.LastAccessed;
}
