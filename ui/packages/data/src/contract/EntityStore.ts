import { type } from "@open-smc/serialization/src/type";
import { LayoutAreaReference } from "./LayoutAreaReference";
import { Collection } from "./Collection";

@type("MeshWeaver.Data.EntityStore")
export class EntityStore {
    reference: LayoutAreaReference;
    collections: Collection<Collection>;
}