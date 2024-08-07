import { type } from "@open-smc/serialization/src/type";

@type("MeshWeaver.Data.DataChangeResponse")
export class DataChangeResponse {
    constructor(public status: DataChangeStatus) {
    }
}

export type DataChangeStatus = "Committed" | "Failed";