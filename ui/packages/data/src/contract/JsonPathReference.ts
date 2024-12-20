import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("MeshWeaver.Data.JsonPathReference")
export class JsonPathReference extends WorkspaceReference {
    constructor(public path: string) {
        super();
    }
}