import { trimStart } from "lodash-es";

export const pointerToArray = (path: string) => trimStart(path, "/").split("/");