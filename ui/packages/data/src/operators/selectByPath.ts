import { get } from "lodash-es";
import { pointerToArray } from "./pointerToArray";

export const selectByPath = <T>(path: string): (data: unknown) => T => {
    const array = pointerToArray(path);
    return (value: unknown) => array ? get(value, array) : value;
}