import {useElementsStore, useNotebookEditorSelector, useNotebookEditorStore} from "../../NotebookEditor";
import { getCompiler, getParser } from "../../markdownParser";
import { ElementMarkdown } from "../documentState";
import { useProjectSelector } from "../../../project/projectStore/projectStore";
import { useCallback } from "react";

export function useUpdateMarkdown() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const projectId = useNotebookEditorSelector("projectId");
    const envId = useNotebookEditorSelector("envId");
    const activeFile = useProjectSelector("activeFile");

    return useCallback((updatedElementIds?: string[]) => {
        const {elementIds, markdown} = notebookEditorStore.getState();
        const models = elementsStore.getState();
        const parse = getParser(true, projectId, envId, activeFile.path);
        const compile = getCompiler(true);

        notebookEditorStore.setState(state => {
            const newMarkdown: Record<string, ElementMarkdown> = {};

            for (let elementId of elementIds) {
                const {element: {elementKind, content}} = models[elementId];

                if (elementKind !== 'markdown') {
                    continue;
                }

                const parsedMarkdown = updatedElementIds?.includes(elementId) || !markdown?.[elementId]
                    ? parse(content) : markdown[elementId].parsedMarkdown;

                const html = compile(parsedMarkdown);

                newMarkdown[elementId] = {
                    parsedMarkdown,
                    html
                };
            }

            state.markdown = newMarkdown;
        });
    }, [notebookEditorStore, elementsStore, envId, projectId, activeFile]);
}