import { JSONPath } from "jsonpath-plus";

export abstract class WorkspaceReference<T = unknown> {
    protected constructor(public path: string) {
    }

    select(data: any): T {
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