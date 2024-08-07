import { type } from "@open-smc/serialization/src/type";
import { DataChangeRequest } from "./DataChangeRequest";
import { WorkspaceReference } from "./WorkspaceReference";
import { JsonPatch } from "./JsonPatch";

@type("MeshWeaver.Data.PatchChangeRequest")
export class PatchChangeRequest extends DataChangeRequest {
    constructor(
        public address: unknown,
        public reference: WorkspaceReference,
        public change: JsonPatch
    ) {
        super();
    }
}