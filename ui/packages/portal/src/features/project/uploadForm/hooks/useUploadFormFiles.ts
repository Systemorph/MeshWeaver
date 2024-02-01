import { UploadFormState, useSelector } from "../store";

export const filesSelector = (state: UploadFormState) => state.files;

export function useUploadFormFiles() {
    return useSelector(filesSelector);
}