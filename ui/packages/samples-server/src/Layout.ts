import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl.ts";
import { Collection } from "@open-smc/data/src/contract/Collection.ts";

export class Layout {
    views: Collection<UiControl> = {};

    addView(area: string, control: UiControl) {
        this.views[area] = control;
        return this;
    }
}

export const uiControlType = (UiControl as any).$type;