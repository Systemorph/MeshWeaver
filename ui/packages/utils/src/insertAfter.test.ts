import {expect, jest, test} from '@jest/globals';
import { insertAfter } from "./insertAfter";

describe("insertAfter", () => {
    test.each([
        [
            "insert at the end",
            ["1", "2", "3"],
            "4",
            "3",
            ["1", "2", "3", "4"]
        ],
        [
            "insert into the middle",
            ["1", "2", "3"],
            "4",
            "2",
            ["1", "2", "4", "3"]
        ],
        [
            "insert at the beginning",
            ["1", "2", "3"],
            "4",
            null,
            ["4", "1", "2", "3"]
        ],
        [
            "wrong afterElement argument",
            ["1", "2", "3"],
            "4",
            "5",
            ["4", "1", "2", "3"]
        ]
    ])("%s", (name: string, elements: string[], element: string, afterElement: string, expected: string[]) => {
        expect(insertAfter(elements, element, afterElement)).toEqual(expected);
    });
});

