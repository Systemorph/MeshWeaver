using System.Collections.Generic;
using System.ComponentModel;

using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>The overall shape of the user home's catalog region.</summary>
public enum HomeStyle
{
    /// <summary>The tabbed home — Shared with me · Pinned · Apps · Spaces (the default).</summary>
    Tabs,

    /// <summary>The legacy single flat catalog list (everything in one search control).</summary>
    Catalog,
}

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
    /// <summary>The home's overall shape: the tabbed home (Shared with me · Pinned · Apps · Spaces) or the legacy flat catalog.</summary>
    [Description("The home's overall shape. Tabs shows Shared with me, Pinned, Apps and Spaces as separate tabs; Catalog is the legacy single flat list.")]
    [Translation("de", "Die Grundform der Startseite. Tabs zeigt Mit mir geteilt, Angeheftet, Apps und Spaces als eigene Tabs; Catalog ist die klassische einzelne Liste.")]
    public HomeStyle Style { get; init; } = HomeStyle.Tabs;

    /// <summary>
    /// The platform's default apps — node paths (usually Store plugin covers, e.g. <c>Store</c>,
    /// <c>Doc</c>) every user's Apps tab starts with. An entry starting with <c>~/</c> is not a
    /// node but an AREA on the viewer's own hub (<c>~/Chat</c> → the Threads app at
    /// <c>/{owner}/Chat</c>), rendered as a fixed system tile. Rendered live from config (no
    /// seeding), so an admin's edit updates every home. Users add more apps by installing from the
    /// Store, which writes <c>{user}/_App/{appId}</c> records.
    /// </summary>
    [Description("The default apps every user's Apps tab starts with — node paths such as Store or Doc, or ~/-prefixed viewer areas such as ~/Chat (the Threads app). Users add more by installing from the Store.")]
    [Translation("de", "Die Standard-Apps, mit denen der Apps-Tab jedes Benutzers startet — Knotenpfade wie Store oder Doc, oder ~/-Einträge für eigene Bereiche wie ~/Chat (die Threads-App). Weitere Apps kommen über den Store hinzu.")]
    // Browsable(false): the generic node-content edit form has no list-capable field kind yet — a
    // Text field would write a plain string into this list-typed slot and corrupt the config. Until
    // a list field ships, admins edit DefaultApps on the node content directly (MCP patch /
    // edit_content on Admin/HomeConfig).
    [Browsable(false)]
    public IReadOnlyList<string> DefaultApps { get; init; } = ["Store", "Doc", "~/Chat"];

    /// <summary>Depth of the home listing: FirstLevel (top-level entries only) or Subtree (the full tree).</summary>
    [Description("How deep the home lists content. FirstLevel shows only top-level entries (the spaces, courses and plugins you can see, plus your own top-level home items). Subtree shows everything you can read.")]
    [Translation("de", "Wie tief die Startseite Inhalte auflistet. FirstLevel zeigt nur Einträge der obersten Ebene (sichtbare Spaces, Kurse und Plugins sowie eigene Einträge der obersten Ebene). Subtree zeigt alles Lesbare.")]
    public HomeCatalogScope Scope { get; init; } = HomeCatalogScope.FirstLevel;

    /// <summary>How items are rendered: Flat (one list) or Grouped (per-type sections).</summary>
    [Description("How the home renders items. Flat is one list; Grouped shows collapsible per-type sections.")]
    [Translation("de", "Wie die Startseite Einträge darstellt. Flat ist eine einzelne Liste; Grouped zeigt einklappbare Abschnitte pro Typ.")]
    public HomeCatalogRender Render { get; init; } = HomeCatalogRender.Flat;

    /// <summary>The default ordering (the user can still change it via the Sort-by control).</summary>
    [Description("The default ordering. Last accessed shows your recently-opened items first; Last modified shows recent edits; Alphabetical sorts by name.")]
    [Translation("de", "Die Standardsortierung. Zuletzt geöffnet zeigt kürzlich geöffnete Einträge zuerst; Zuletzt geändert zeigt kürzliche Änderungen; Alphabetisch sortiert nach Name.")]
    public HomeCatalogSort DefaultSort { get; init; } = HomeCatalogSort.LastAccessed;
}
