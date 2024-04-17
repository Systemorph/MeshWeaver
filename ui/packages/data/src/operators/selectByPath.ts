import { get } from "lodash-es";
import { pointerToArray } from "./pointerToArray";

export const selectByPath = <T>(path: string): (data: unknown) => T =>
    (value: unknown) =>
        path ? get(value, pointerToArray(path)) : value