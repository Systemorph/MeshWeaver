import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";
import { Collection } from "./Collection";

@type("OpenSmc.Data.CollectionReference")
export class CollectionReference<T> extends WorkspaceReference<Collection<T>> {
    constructor(public collection: string) {
        super(`/${collection}`);
    }

    get(data: any) {
        return data?.[this.collection] as Collection<T>;
    }

    static create<T>(props: CollectionReference<T>) {
        return new CollectionReference<T>(props.collection);
    }
}