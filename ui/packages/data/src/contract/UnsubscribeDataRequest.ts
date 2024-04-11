import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReferenceBase } from "./WorkspaceReferenceBase";

@type("OpenSmc.Data.UnsubscribeDataRequest")
export class UnsubscribeDataRequest {
    constructor(public reference: WorkspaceReferenceBase) {
    }
}