import type { AreaTree } from "../area/types.js";

const ref = (area: string) => ({ $type: "NamedArea", area });
const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });

// A self-contained area tree exercising the control set — the same {areas,data} shape a live
// MeshWeaver portal streams. Form inputs bind to /data and edit optimistically via StaticAreaSource.
export const sampleArea: AreaTree = {
  data: {
    name: "Ada Lovelace",
    role: "Engineer",
    active: true,
    level: 70,
    when: "2026-06-30",
    rows: [
      { name: "ACME", amount: 124000, status: "Active" },
      { name: "Northwind", amount: 98230, status: "Active" },
      { name: "Cornerstone", amount: 54100, status: "Paused" },
    ],
    roles: [
      { item: "Engineer", text: "Engineer" },
      { item: "Designer", text: "Designer" },
      { item: "PM", text: "Product Manager" },
    ],
  },
  areas: {
    main: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", orientation: "Vertical", verticalGap: 20 }],
      style: { padding: 24, maxWidth: 980, margin: "0 auto" },
      areas: [ref("header"), ref("intro"), ref("metrics"), ref("tabs"), ref("formCard"), ref("dndCard"), ref("chartCard"), ref("navCard"), ref("feedbackCard"), ref("footer")],
    },
    header: { $type: "Label", typo: "PageTitle", data: "MeshWeaver × Fluent UI" },
    intro: {
      $type: "Markdown",
      data: "The **same `UiControl` tree** the Blazor portal and MAUI render — now in React + Fluent UI. Edit a field below; the binding writes back through the area's `/data` pointer.",
    },

    metrics: {
      $type: "LayoutGrid",
      skins: [{ $type: "LayoutGrid", spacing: 12 }],
      areas: [ref("m1"), ref("m2"), ref("m3")],
    },
    m1: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("m1t"), ref("m1v")] },
    m1t: { $type: "Label", data: "Open PRs" },
    m1v: { $type: "Label", typo: "Header", data: "1" },
    m2: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("m2t"), ref("m2v")] },
    m2t: { $type: "Label", data: "Controls ported" },
    m2v: { $type: "Label", typo: "Header", data: "50+" },
    m3: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("m3t"), ref("m3v")] },
    m3t: { $type: "Label", data: "Status" },
    m3v: { $type: "Badge", data: "Green" },

    tabs: { $type: "Tabs", skins: [{ $type: "Tabs" }], areas: [ref("tabOverview"), ref("tabData")] },
    tabOverview: {
      $type: "Stack",
      skins: [{ $type: "Tab", label: "Overview" }],
      areas: [ref("tabOverviewBody")],
    },
    tabOverviewBody: { $type: "Markdown", data: "Tabs, stacks, grids, cards — all skins of the **same container** model." },
    tabData: { $type: "Stack", skins: [{ $type: "Tab", label: "Data" }], areas: [ref("datagrid")] },
    datagrid: {
      $type: "DataGrid",
      data: ptr("/data/rows"),
      columns: [
        { $type: "PropertyColumn", property: "name", title: "Account" },
        { $type: "PropertyColumn", property: "amount", title: "Amount", format: "N0" },
        { $type: "PropertyColumn", property: "status", title: "Status" },
      ],
    },

    formCard: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("formTitle"), ref("name"), ref("role"), ref("active"), ref("level"), ref("when"), ref("saveBtn")] },
    formTitle: { $type: "Label", typo: "Subject", data: "Profile (data-bound)" },
    name: { $type: "TextField", label: "Name", data: ptr("/data/name") },
    role: { $type: "Select", label: "Role", data: ptr("/data/role"), options: ptr("/data/roles") },
    active: { $type: "CheckBox", label: "Active", data: ptr("/data/active") },
    level: { $type: "Slider", min: 0, max: 100, step: 5, data: ptr("/data/level") },
    when: { $type: "Date", label: "Joined", data: ptr("/data/when") },
    saveBtn: { $type: "Button", data: "Save", appearance: "primary", iconStart: "Save", isClickable: true },

    dndCard: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("dndTitle"), ref("dndRow")] },
    dndTitle: { $type: "Label", typo: "Subject", data: "Drag & drop" },
    dndRow: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", orientation: "Horizontal", horizontalGap: 16 }],
      areas: [ref("dragCard"), ref("dropZone")],
    },
    dragCard: { $type: "Draggable", payload: "card-1", contentArea: ref("dragCardContent") },
    dragCardContent: { $type: "Label", data: "Drag me" },
    dropZone: { $type: "DropTarget", contentArea: ref("dropZoneContent") },
    dropZoneContent: { $type: "Label", data: "Drop here" },

    chartCard: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("chart")] },
    chart: {
      $type: "Chart",
      title: "Revenue by quarter",
      labels: ["Q1", "Q2", "Q3", "Q4"],
      series: [
        { name: "2025", data: [10, 20, 15, 30] },
        { name: "2026", data: [12, 18, 22, 28] },
      ],
    },

    navCard: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("nav")] },
    nav: { $type: "NavMenu", skins: [{ $type: "NavMenu", width: 240 }], areas: [ref("navHome"), ref("navGroup")] },
    navHome: { $type: "NavLink", title: "Home", icon: "Home", url: "#", isActive: true },
    navGroup: { $type: "NavGroup", skins: [{ $type: "NavGroup", title: "Spaces", expanded: true }], areas: [ref("navAcme"), ref("navNorth")] },
    navAcme: { $type: "NavLink", title: "ACME", url: "#" },
    navNorth: { $type: "NavLink", title: "Northwind", url: "#" },

    feedbackCard: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("progress"), ref("spinner"), ref("iconRow")] },
    progress: { $type: "Progress", message: "Indexing…", progress: 65 },
    spinner: { $type: "Spinner", message: "Loading…" },
    iconRow: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", orientation: "Horizontal", horizontalGap: 10 }],
      areas: [ref("iconHeart"), ref("iconStar"), ref("badgeNew")],
    },
    iconHeart: { $type: "Icon", data: "Heart" },
    iconStar: { $type: "Icon", data: "Star" },
    badgeNew: { $type: "Badge", data: "New" },

    footer: { $type: "Markdown", data: "_Rendered by `@meshweaver/react` — the same UiControl tree as Blazor & MAUI._" },
  },
};
