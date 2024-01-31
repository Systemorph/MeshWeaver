import { getCompiler, getParser } from "../markdownParser";
import { ElementMarkdown, NotebookElementModel } from "./documentState";

export function getDocumentMarkdown(
    elementIds: string[],
    models: Record<string, NotebookElementModel>,
    projectId: string,
    environmentId: string,
    path: string
) {
    const parse = getParser(true, projectId, environmentId, path);
    const compile = getCompiler(false);

    const result: Record<string, ElementMarkdown> = {};

    elementIds.forEach(elementId => {
        const {element} = models[elementId];
        if (element.elementKind === 'markdown') {
            const parsedMarkdown = parse(element.content);
            const html = compile(parsedMarkdown);

            result[element.id] = {
                parsedMarkdown,
                html
            };
        }
    });

    return result;
}