import { useNotebookEditorSelector } from "../../NotebookEditor";
import { useElement } from "./useElement";

export function useActiveElement() {
    const activeElementId = useNotebookEditorSelector("activeElementId");
    return useElement(activeElementId);
}