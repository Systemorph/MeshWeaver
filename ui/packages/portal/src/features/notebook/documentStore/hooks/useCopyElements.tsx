import { useNotebookEditorStore } from "../../NotebookEditor";

export function useCopyElements() {
    const {setState} = useNotebookEditorStore();

    return () => {
        setState(({selectedElementIds, clipboard}) => {
            clipboard.elementIds = selectedElementIds;
            clipboard.operation = "cut";
        });
    }
}