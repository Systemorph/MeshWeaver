import { UploadFileModel, useStore } from "../store";
import { filesSelector } from "./useUploadFormFiles";
import { isFunction, keyBy } from "lodash";
import { fileSelectors } from "./useUploadFormFile";

export type UploadFileModelPatch = Partial<Omit<UploadFileModel, 'id'>>;

export function useSetUploadFormFiles() {
    const {getState, setState, notify} = useStore();

    return (fileIds: string[], patch: UploadFileModelPatch | ((file: UploadFileModel) => UploadFileModelPatch)) => {
        const {files: uploadFormFiles} = getState();

        const patchedFiles = fileIds
            .map<UploadFileModel>(fileId => {
                const file = uploadFormFiles[fileId];

                if (!file) {
                    throw `File not found - ${fileId}`;
                }

                return {
                    ...file,
                    ...(isFunction(patch) ? patch(file) : patch)
                };
            });

        const newFiles = {...uploadFormFiles, ...keyBy(patchedFiles, 'id')};

        setState({files: newFiles});

        patchedFiles.forEach(({id}) => notify(fileSelectors(id)));
        notify(filesSelector);
    }
}