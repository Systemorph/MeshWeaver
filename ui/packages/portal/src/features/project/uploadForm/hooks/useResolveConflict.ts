import { ConflictResolution } from "../store";
import { useUploadFormFiles } from "./useUploadFormFiles";
import { useSetUploadFormFiles } from "./useSetUploadFormFiles";
import { map, values } from "lodash";
import { useStartUpload } from "./useStartUpload";

export function useResolveConflict() {
    const uploadFormFiles = useUploadFormFiles();
    const setUploadFormFiles = useSetUploadFormFiles();
    const tryUploadFile = useStartUpload();

    return (fileId: string, conflictResolution: ConflictResolution, applyToAll: boolean) => {
        const file = uploadFormFiles[fileId];

        if (!file.conflict || file.conflictResolution) {
            throw 'Conflict is already resolved';
        }

        const fileIds = applyToAll
            ? map(values(uploadFormFiles).filter(({conflict, conflictResolution}) => conflict && !conflictResolution), 'id')
            : [fileId];

        setUploadFormFiles(fileIds, ({blob: {name}}) => {
            return {
                conflictResolution
            }
        });

        fileIds.forEach(tryUploadFile);
    }
}

