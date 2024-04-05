import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.DataChangedEvent")
export class DataChangedEvent {
    constructor(
        public reference: WorkspaceReference,
        public change: unknown,
        public changeType: ChangeType
    ) {
    }
}

export type ChangeType = "Full" | "Patch" | "Instance";