import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.EntireWorkspace")
export class EntireWorkspace extends WorkspaceReference {
    constructor() {
        super();
    }
}