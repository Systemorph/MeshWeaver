import { JSONPath } from "jsonpath-plus";
import { WorkspaceReference } from "./contract/WorkspaceReference";
import { JsonPathReference } from "./contract/JsonPathReference";
import { EntityReference } from "./contract/EntityReference";
import { CollectionReference } from "./contract/CollectionReference";

export const selectByReference = <T = unknown>(reference: WorkspaceReference) =>
    (data: any): T => {
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
        if (reference instanceof EntityReference) {
            return data?.[reference.collection]?.[reference.id];
        }
        if (reference instanceof CollectionReference) {
            return data?.[reference.collection];
        }
    }