import { type } from "@open-smc/serialization/src/type";
import { PathReferenceBase } from "./PathReferenceBase";
import { Collection } from "./Collection";

@type("OpenSmc.Data.CollectionReference")
export class CollectionReference<T> extends PathReferenceBase<Collection<T>> {
    constructor(public collection: string) {
        super();
    }

    protected get path() {
        return `/${this.collection}`;
    }
}