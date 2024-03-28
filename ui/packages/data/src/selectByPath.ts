import { get } from "lodash-es";

export const selectByPath = (path: string) =>
    (value: unknown) => get(value, path);