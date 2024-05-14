import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl.ts";

export const uiControlType = (UiControl as any).$type;

export class LayoutViews extends Map<string, UiControl> {
    addView(area: string, control: UiControl) {
        this.set(area, control);
        return new EntityReference(uiControlType, area);
    }

    toCollection() {
        return Object.fromEntries(this);
    }
}