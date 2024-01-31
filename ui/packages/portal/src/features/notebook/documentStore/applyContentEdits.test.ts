import { applyContentEdits } from "./applyContentEdits";
import { NotebookElementChangeData } from "../notebookEditor/notebookEditor.contract";

describe("applyEdits", () => {
    test.each([
        [
            "Hello world",
            [
                {startLineNumber: 0, startColumn: 11, endLineNumber: 0, endColumn: 11, text: "\n"},
                {startLineNumber: 1, startColumn: 0, endLineNumber: 1, endColumn: 0, text: "!"},
            ],
            "Hello world\n!",
        ],
        [
            "Hello world",
            [
                {startLineNumber: 0, startColumn: 5, endLineNumber: 0, endColumn: 6, text: "\nnew "},
                {startLineNumber: 1, startColumn: 4, endLineNumber: 1, endColumn: 9, text: "day"},
                {startLineNumber: 1, startColumn: 3, endLineNumber: 1, endColumn: 4, text: "\n"},
                {startLineNumber: 2, startColumn: 0, endLineNumber: 2, endColumn: 1, text: "D"},
            ],
            "Hello\nnew\nDay",
        ],
    ])("applyElementChanges %s %s", (content: string, edits: NotebookElementChangeData[], expected: string) => {
        expect(applyContentEdits(content, edits)).toBe(expected);
    });
});

