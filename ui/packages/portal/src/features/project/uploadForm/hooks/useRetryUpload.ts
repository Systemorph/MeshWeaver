import { useStore } from "../store";
import { useSetUploadFormFile } from "./useSetUploadFormFile";
import { useNodeExists } from "../../projectExplorer/projectExplorerStore/hooks/useNodeExists";
import { useStartUpload } from "./useStartUpload";

export function useRetryUpload() {
    const {getState} = useStore();
    const nodeExists = useNodeExists();
    const setUploadFormFile = useSetUploadFormFile();
    const startUpload = useStartUpload();

    return (fileId: string) => {
        const {files} = getState();

        const {exists: conflict, canReplace} = nodeExists(files[fileId].blob.name);

        const status = !conflict || canReplace ? 'New' : 'Error';

        setUploadFormFile(fileId, {
            status,
            conflict,
            conflictResolution: null,
            newName: null
        });

        if (status === 'New' && !conflict) {
            startUpload(fileId);
        }
    }
}