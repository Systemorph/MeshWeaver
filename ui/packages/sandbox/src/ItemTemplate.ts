import type { ItemTemplateView } from "@open-smc/application/controls/ItemTemplateControl";
import { StackSkin } from "@open-smc/application/controls/LayoutStackControl";
import { ControlDef } from "@open-smc/application/ControlDef";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class ItemTemplate extends ControlBase implements ItemTemplateView {
    skin: StackSkin;

    constructor(public data: unknown[], public view: ControlDef) {
        super("ItemTemplateControl");
    }
}

class ItemTemplateBuilder extends ControlBuilderBase<ItemTemplate> {
    constructor() {
        super(ItemTemplate);
    }

    withView(view: ControlDef) {
        this.data.view = view;
        return this;
    }

    withSkin(value: StackSkin) {
        return super.withSkin(value);
    }
}

export const makeItemTemplate = () => new ItemTemplateBuilder();