import { useSetUploadFormFiles } from "./useSetUploadFormFiles";
import { useStore } from "../store";
import { values } from "lodash";

export function useCancelAll() {
    const {getState} = useStore();
    const setUploadFormFiles = useSetUploadFormFiles();

    return () => {
        const {files} = getState();
        const filesArray = values(files);

        filesArray.filter(({status}) => status === 'InProgress')
            .forEach(({cancelTokenSource}) => cancelTokenSource.cancel());

        setUploadFormFiles(
            filesArray.filter(({status}) => status === 'New' || status === 'InProgress').map(({id}) => id),
            {status: 'Canceled'}
        );
    }
}