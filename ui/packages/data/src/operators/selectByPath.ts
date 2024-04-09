import { get } from "lodash-es";

export const selectByPath = (path: string) =>
    (value: unknown) => path ? get(value, path) : value;