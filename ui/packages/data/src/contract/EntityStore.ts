import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { LayoutAreaReference } from "./LayoutAreaReference";

@type("OpenSmc.Data.EntityStore")
export class EntityStore {
    reference: LayoutAreaReference;
    instances: Record<string, Record<string, UiControl>>
}