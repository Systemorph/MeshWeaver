import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";
import { deserialize } from "@open-smc/serialization/src/deserialize";

@type("OpenSmc.Data.DataChangedEvent")
export class DataChangedEvent {
    constructor(
        public reference: WorkspaceReference,
        public change: unknown,
        public changeType: ChangeType,
        public changedBy: unknown
    ) {
    }

    // keeping change raw since the patch is meant to be applied to the raw json as-is
    static deserialize(props: DataChangedEvent) {
        const {reference, change, changeType, changedBy} = props;
        return new DataChangedEvent(deserialize(reference), change, changeType, changedBy);
    }
}

export type ChangeType = "Full" | "Patch" | "Instance";