import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.CollectionReference")
export class CollectionReference<T> extends WorkspaceReference<T> {
    constructor(public collection: string) {
        super(`$.${collection}`);
    }

    apply = (data: any) => data?.[this.collection];
}