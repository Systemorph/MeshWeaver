import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";
import { DataChangeRequest } from "./DataChangeRequest";

@type("OpenSmc.Data.PatchChangeRequest")
export class PatchChangeRequest extends DataChangeRequest {
    constructor(
        public address: unknown,
        public reference: WorkspaceReference,
        public change: object
    ) {
        super();
    }
}