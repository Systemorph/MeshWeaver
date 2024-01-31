import { flatten, identity } from "lodash";
import { useNotebookEditorSelector } from "../../NotebookEditor";

export function useHeadings() {
    const markdown = useNotebookEditorSelector("markdown");
    const elementIds = useNotebookEditorSelector("elementIds");
    return flatten(elementIds.map(elementId => markdown?.[elementId]?.parsedMarkdown?.headings))
        .filter(identity);
}