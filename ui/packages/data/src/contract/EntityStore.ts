import { type } from "@open-smc/serialization/src/type";
import { LayoutAreaReference } from "./LayoutAreaReference";
import { Collection } from "./Collection";

@type("OpenSmc.Data.EntityStore")
export class EntityStore {
    reference: LayoutAreaReference;
    collections: Collection<Collection>;
}