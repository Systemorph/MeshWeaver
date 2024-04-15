import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";
import { deserialize } from "@open-smc/serialization/src/deserialize";

@type("OpenSmc.Data.DataChangedEvent")
export class DataChangedEvent {
    constructor(
        public reference: WorkspaceReference,
        public change: unknown,
        public changeType: ChangeType
    ) {
    }

    // // keeping change raw since the patch is meant to be applied to the json store as-is
    // static deserialize(props: DataChangedEvent) {
    //     const {reference, change, changeType} = props;
    //     return new DataChangedEvent(deserialize(reference), change, changeType);
    // }
}

export type ChangeType = "Full" | "Patch" | "Instance";