import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("MeshWeaver.Data.CollectionReference")
export class CollectionReference extends WorkspaceReference {
    constructor(public collection: string) {
        super();
    }
}