import { JsonPointer } from "json-ptr";

export const selectByPath = <T>(path: string): (data: unknown) => T =>
    (value: unknown) => JsonPointer.get(value, path) as T;