import { type } from "@open-smc/serialization/src/type";
import { JSONPath } from "jsonpath-plus";
import { WorkspaceReference } from "./WorkspaceReference";
import { toPointer } from "../toPointer";
import { pointerToArray } from "../operators/pointerToArray";
import { set } from "lodash-es";

@type("OpenSmc.Data.JsonPathReference")
export class JsonPathReference<T> extends WorkspaceReference<T> {
    constructor(public jsonPath: string) {
        super();
    }

    // get(data: any): T {
    //     // jsonpath-plus returns undefined if data is empty string or 0
    //     if (this.jsonPath === "$") {
    //         return data;
    //     }
    //
    //     return JSONPath(
    //         {
    //             json: data,
    //             path: this.jsonPath,
    //             wrap: false
    //         }
    //     );
    // }
    //
    // set(data: object, value: T) {
    //     const pointer = toPointer(this.jsonPath);
    //     set(data, pointerToArray(pointer), value);
    // }
}