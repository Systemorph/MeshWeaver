import { EntireWorkspace, JsonPathReference, WorkspaceReference } from "./data.contract";
import { map, Observable } from "rxjs";
import { isOfType } from "@open-smc/application/src/contract/ofType";
import { JSONPath } from "jsonpath-plus";

export const expandReference = (workspaceReference: WorkspaceReference) =>
    (source: Observable<unknown>) =>
        source
            .pipe(
                map(
                    value => {
                        if (isOfType(value, EntireWorkspace)) {
                            return value;
                        }
                        if (isOfType(value, JsonPathReference)) {
                            return JSONPath({path: value.path, json: value});
                        }
                    }
                )
            );