import { NotebookElementChangeData } from "../notebookElement.contract";

export function applyContentEdits(content: string, changes: NotebookElementChangeData[]) {
    for (const change of changes) {
        const {startColumn, endColumn, text} = change;
        let {startLineNumber, endLineNumber} = change;

        let startOffset = 0;
        let endOffset = 0;

        if (startLineNumber > 0 || endLineNumber > 0) {
            for (let i = 0; i < content.length; i++) {
                if (content[i] === "\n") {
                    if (startLineNumber > 0) {
                        if (--startLineNumber === 0) {
                            startOffset = i + 1;
                        }
                    }
                    if (endLineNumber > 0) {
                        if (--endLineNumber === 0) {
                            endOffset = i + 1;
                        }
                    }
                }
            }

            if (startLineNumber > 0 || endLineNumber > 0) {
                throw 'Wrong element content range';
            }
        }

        content = content.substring(0, startOffset + startColumn) + text + content.substring(endOffset + endColumn);
    }

    return content;
}