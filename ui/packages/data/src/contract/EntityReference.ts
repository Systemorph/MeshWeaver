import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.EntityReference")
export class EntityReference extends WorkspaceReference {
    constructor(public collection: string, public id: string) {
        super();
    }
}