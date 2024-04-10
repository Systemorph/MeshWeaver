import { UiControl } from "./UiControl";
import { type } from "@open-smc/serialization/src/type";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";

@type("OpenSmc.Layout.Composition.LayoutStackControl")
export class LayoutStackControl extends UiControl {
    constructor(public areas: EntityReference<UiControl>[]) {
        super();
    }
}