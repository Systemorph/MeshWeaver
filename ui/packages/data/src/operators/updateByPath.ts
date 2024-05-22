import { set } from "lodash-es";
import { pointerToArray } from "./pointerToArray";

export const updateByPath = <T>(data: any, path: string, value: any) =>
    set(data, pointerToArray(path), value);