import { moveElements } from "./moveElements";

describe("moveElements", () => {
    test.each([
        [
            "move down",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            ["5"],
            "6",
            ["1", "2", "3", "4", "6", "5", "7", "8", "9"]
        ],
        [
            "move up",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            ["5"],
            "3",
            ["1", "2", "3", "5", "4", "6", "7", "8", "9"]
        ],
        [
            "move to the beginning",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            ["2", "3"],
            null,
            ["2", "3", "1", "4", "5", "6", "7", "8", "9"]
        ],
        [
            "move to the end",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            ["6", "7", "8"],
            "9",
            ["1", "2", "3", "4", "5", "9", "6", "7", "8"]
        ]
    ])("%s", (name: string, elementIds: string[], elementIdsToMove: string[], afterElementId: string, expected: string[]) => {
        expect(moveElements(elementIds, elementIdsToMove, afterElementId)).toEqual(expected);
    });
});

describe("moveElements throws", () => {
    test.each([
        [
            "unknown elements",
            ["1", "2", "3"],
            ["4", "5"],
            "2"
        ],
        [
            "wrong order of elements",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            ["4", "2", "3"],
            null
        ],
        [
            "skipped element",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            [ "2", "4"],
            null
        ],
        [
            "afterElementId is one of the moved elements",
            ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
            ["6", "7", "8"],
            "7",
        ],
        [
            "afterElementId not found",
            ["1", "2", "3"],
            ["2"],
            "7",
        ],
    ])("%s", (name: string, elementIds: string[], elementIdsToMove: string[], afterElementId: string) => {
        expect(() => moveElements(elementIds, elementIdsToMove, afterElementId)).toThrow();
    });
});