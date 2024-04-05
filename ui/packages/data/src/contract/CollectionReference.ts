import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.CollectionReference")
export class CollectionReference extends WorkspaceReference {
    constructor(public collection: string) {
        super();
    }

    toJsonPath(): string {
        return `$.${this.collection}`;
    }
}