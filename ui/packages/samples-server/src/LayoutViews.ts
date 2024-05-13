import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl.ts";

export class LayoutViews extends Map<string, UiControl> {
    addView(area: string, control: UiControl) {
        this.set(area, control);
        return this;
    }

    toCollection() {
        return Object.fromEntries(this);
    }
}