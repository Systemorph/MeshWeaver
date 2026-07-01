import type { AreaTree } from "@meshweaver/react";

const ref = (area: string) => ({ $type: "NamedArea", area });
const ptr = (pointer: string) => ({ $type: "JsonPointerReference", pointer });

// Three root areas the nav switches between — a dashboard, a data grid, and a data-bound form. Same
// {areas,data} shape the mesh streams; the portal just picks which root to render.
export const portalAreas: AreaTree = {
  data: {
    name: "Ada Lovelace",
    role: "Engineer",
    active: true,
    level: 70,
    when: "2026-06-30",
    roles: [
      { item: "Engineer", text: "Engineer" },
      { item: "Designer", text: "Designer" },
      { item: "PM", text: "Product Manager" },
    ],
    rows: [
      { name: "ACME", amount: 124000, status: "Active" },
      { name: "Northwind", amount: 98230, status: "Active" },
      { name: "Cornerstone", amount: 54100, status: "Paused" },
      { name: "Fabrikam", amount: 32750, status: "Active" },
    ],
  },
  areas: {
    // ── Dashboard ─────────────────────────────────────────────────────────────
    home: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", verticalGap: 20 }],
      areas: [ref("homeTitle"), ref("homeIntro"), ref("metrics"), ref("chartCard")],
    },
    homeTitle: { $type: "Label", typo: "PageTitle", data: "Dashboard" },
    homeIntro: { $type: "Markdown", data: "A MeshWeaver portal — header, nav, and a **layout area** rendered with the same `UiControl` tree as Blazor & MAUI." },
    metrics: { $type: "LayoutGrid", skins: [{ $type: "LayoutGrid", spacing: 12 }], areas: [ref("m1"), ref("m2"), ref("m3")] },
    m1: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("m1t"), ref("m1v")] },
    m1t: { $type: "Label", data: "Accounts" },
    m1v: { $type: "Label", typo: "Header", data: "4" },
    m2: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("m2t"), ref("m2v")] },
    m2t: { $type: "Label", data: "Pipeline" },
    m2v: { $type: "Label", typo: "Header", data: "$309k" },
    m3: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("m3t"), ref("m3v")] },
    m3t: { $type: "Label", data: "Status" },
    m3v: { $type: "Badge", data: "Healthy" },
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

    // ── Accounts (data grid) ────────────────────────────────────────────────────
    accounts: { $type: "Stack", skins: [{ $type: "LayoutStack", verticalGap: 16 }], areas: [ref("accTitle"), ref("accGrid")] },
    accTitle: { $type: "Label", typo: "PageTitle", data: "Accounts" },
    accGrid: {
      $type: "DataGrid",
      data: ptr("/data/rows"),
      columns: [
        { $type: "PropertyColumn", property: "name", title: "Account" },
        { $type: "PropertyColumn", property: "amount", title: "Amount", format: "C0" },
        { $type: "PropertyColumn", property: "status", title: "Status" },
      ],
    },

    // ── Profile (data-bound form) ───────────────────────────────────────────────
    profile: { $type: "Stack", skins: [{ $type: "LayoutStack", verticalGap: 16 }], areas: [ref("profTitle"), ref("formCard")] },
    profTitle: { $type: "Label", typo: "PageTitle", data: "Profile" },
    formCard: { $type: "Stack", skins: [{ $type: "Card" }], areas: [ref("fName"), ref("fRole"), ref("fActive"), ref("fLevel"), ref("fWhen"), ref("fSave")] },
    fName: { $type: "TextField", label: "Name", data: ptr("/data/name") },
    fRole: { $type: "Select", label: "Role", data: ptr("/data/role"), options: ptr("/data/roles") },
    fActive: { $type: "CheckBox", label: "Active", data: ptr("/data/active") },
    fLevel: { $type: "Slider", min: 0, max: 100, step: 5, data: ptr("/data/level") },
    fWhen: { $type: "Date", label: "Joined", data: ptr("/data/when") },
    fSave: { $type: "Button", data: "Save", appearance: "primary", iconStart: "Save", isClickable: true },
  },
};
