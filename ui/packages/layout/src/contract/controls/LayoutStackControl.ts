import { UiControl } from "./UiControl";
import { type } from "@open-smc/serialization/src/type";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";

@type("OpenSmc.Layout.Composition.LayoutStackControl")
export class LayoutStackControl extends UiControl<LayoutStackControl> {
    areas: EntityReference[];
    skin: LayoutStackSkin;
    highlightNewAreas?: boolean;
    columnCount?: number;
}

export type LayoutStackSkin =
    "VerticalPanel"
    | "HorizontalPanel"
    | "HorizontalPanelEqualCols"
    | "Toolbar"
    | "SideMenu"
    | "ContextMenu"
    | "MainWindow"
    |
    "Action"
    | "Modal"
    | "GridLayout";