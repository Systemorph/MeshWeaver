import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("MeshWeaver.Data.UnsubscribeDataRequest")
export class UnsubscribeDataRequest {
    constructor(public reference: WorkspaceReference) {
    }
}