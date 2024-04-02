import { JsonPathReference, WorkspaceReference } from "./data.contract";
import { JSONPath } from "jsonpath-plus";

export const selectByReference = (data: any, reference: WorkspaceReference) => {
    if (reference instanceof JsonPathReference) {
        // jsonpath-plus returns undefined if data is empty string or 0
        if (reference.path === "$") {
            return data;
        }

        return JSONPath(
            {
                json: data,
                path: reference.path,
                wrap: false
            }
        );
    }
}