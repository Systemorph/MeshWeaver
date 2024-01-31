import { values } from "lodash";
import { UploadFileModel } from "../store";
import { useUploadFormFiles } from "./useUploadFormFiles";

export function useSelectFiles(predicate: (file: UploadFileModel) => boolean) {
    const files = useUploadFormFiles();
    return values(files).filter(predicate);
}