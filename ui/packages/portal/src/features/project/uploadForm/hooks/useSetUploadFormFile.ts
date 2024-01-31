import { UploadFileModelPatch, useSetUploadFormFiles } from "./useSetUploadFormFiles";

export function useSetUploadFormFile() {
    const setUploadFormFiles = useSetUploadFormFiles();

    return (fileId: string, patch: UploadFileModelPatch) => {
        setUploadFormFiles([fileId], patch);
    }
}