import { JSONPath } from "jsonpath-plus";

// JsonPath to JsonPointer e.g. "$.obj.property" => "/obj/property"
export const toPointer = (jsonPath: string) =>
    JSONPath.toPointer(
        JSONPath.toPathArray(jsonPath)
    );