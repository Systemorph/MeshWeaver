import { UploadFormState, useSelector } from "../store";

export const fileIdsSelector = (state: UploadFormState) => state.fileIds;

export function useUploadFormFileIds() {
    return useSelector(fileIdsSelector);
}