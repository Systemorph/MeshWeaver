import { basename, extname } from "path-browserify";
import { values } from "lodash";
import { useFileStore } from "../../ProjectExplorerContextProvider";

export const getNodeByFileName = (fileName: string) => {
    const ipynbMatch = /\.ipynb$/i.exec(fileName);
    const isNotebook = !!ipynbMatch;
    const nodeName = isNotebook ? basename(fileName, ipynbMatch[0]) : fileName;
    const ext = extname(fileName);
    return {nodeName, isNotebook, ext};
};

export function useNodeExists() {
    const fileStore = useFileStore();

    return (fileName: string) => {
        const {nodeName, isNotebook} = getNodeByFileName(fileName);
        const files = fileStore.getState();
        const node = values(files).find(f => f.name.toLowerCase() === nodeName.toLowerCase());

        const exists = node !== undefined;

        return {
            exists,
            // do not allow to replace a file of a different kind
            canReplace: exists ? isNotebook === (node.kind === 'Notebook') : undefined
        }
    }
}