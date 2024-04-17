import { type } from "@open-smc/serialization/src/type";
import { Collection } from "./Collection";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.CollectionReference")
export class CollectionReference<T> extends WorkspaceReference<Collection<T>> {
    constructor(public collection: string) {
        super();
    }
    //
    // protected get path() {
    //     return `/${this.collection}`;
    // }
}