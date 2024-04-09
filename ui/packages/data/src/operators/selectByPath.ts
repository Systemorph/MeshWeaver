import { get, trimStart } from "lodash-es";

export const selectByPath = (path: string) => {
    const lodashPath = jsonPointerToLodashPath(path);
    return (value: unknown) => lodashPath ? get(value, lodashPath) : value;
}

const jsonPointerToLodashPath = (path: string) => trimStart(path, "/").split("/").join(".");