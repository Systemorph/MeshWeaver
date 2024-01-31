import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { getCompiler, getParser } from "../../markdownParser";
import { ElementMarkdown } from "../documentState";
import { useProject } from "../../../project/projectStore/hooks/useProject";
import { useEnv } from "../../../project/projectStore/hooks/useEnv";
import { useProjectSelector } from "../../../project/projectStore/projectStore";
import { useCallback } from "react";

export function useUpdateMarkdown() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const {env} = useEnv();
    const {project: {id: projectId}} = useProject();
    const activeFile = useProjectSelector("activeFile");

    return useCallback((updatedElementIds?: string[]) => {
        const {elementIds, markdown} = notebookEditorStore.getState();
        const models = elementsStore.getState();
        const parse = getParser(true, projectId, env.id, activeFile.path);
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
    }, [notebookEditorStore, elementsStore, env, projectId, activeFile]);
}