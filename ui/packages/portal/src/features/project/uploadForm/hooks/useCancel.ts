import { useStore } from "../store";
import { useSetUploadFormFile } from "./useSetUploadFormFile";

export function useCancel() {
    const {getState} = useStore();
    const setUploadFormFile = useSetUploadFormFile();

    return (fileId: string) => {
        const {files} = getState();
        const {status, cancelTokenSource} = files[fileId];

        if (status === 'InProgress') {
            cancelTokenSource.cancel();
        }

        setUploadFormFile(fileId, {status: 'Canceled'});
    }
}