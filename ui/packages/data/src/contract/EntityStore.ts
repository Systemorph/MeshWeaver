import { type } from "@open-smc/serialization/src/type";
import { LayoutAreaReference } from "./LayoutAreaReference";

@type("OpenSmc.Data.EntityStore")
export class EntityStore {
    reference: LayoutAreaReference;
    collections: Record<string, Record<string, unknown>>;
}