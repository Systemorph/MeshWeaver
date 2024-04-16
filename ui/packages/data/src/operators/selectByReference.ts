import { WorkspaceReference } from "../contract/WorkspaceReference";
import { selectByPath } from "./selectByPath";
import { JsonPathReference } from "../contract/JsonPathReference";
import { JSONPath } from "jsonpath-plus";
import { isPathReference } from "./isPathReference";
import { getReferencePath } from "./getReferencePath";

export const selectByReference = <T>(reference: WorkspaceReference<T>) =>
    (data: any) => {
        if (isPathReference(reference)) {
            return selectByPath(getReferencePath(reference))(data);
        }
        if (reference instanceof JsonPathReference) {
            // jsonpath-plus returns undefined if data is empty string or 0
            if (reference.jsonPath === "$") {
                return data;
            }

            return JSONPath(
                {
                    json: data,
                    path: reference.jsonPath,
                    wrap: false
                }
            );
        }
    }

