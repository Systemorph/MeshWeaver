import { UploadFormState, useSelector } from "../store";
import { memoize } from "lodash";

export const fileSelectors = memoize((fileId: string) => (state: UploadFormState) => state.files[fileId]);

export function useUploadFormFile(fileId: string) {
    return useSelector(fileSelectors(fileId));
}

export function clearFileSelectorsCache() {
    fileSelectors.cache.clear();
}