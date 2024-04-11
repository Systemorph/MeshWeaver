import { type } from "@open-smc/serialization/src/type";
import { JSONPath } from "jsonpath-plus";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.JsonPathReference")
export class JsonPathReference<T> extends WorkspaceReference<T> {
    constructor(path: string) {
        super(path);
    }

    get(data: any): T {
        // jsonpath-plus returns undefined if data is empty string or 0
        if (this.path === "$") {
            return data;
        }

        return JSONPath(
            {
                json: data,
                path: this.path,
                wrap: false
            }
        );
    }
}