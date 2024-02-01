import {expect, jest, test} from '@jest/globals';
import { insertBefore } from "./insertBefore";

describe("insertBefore", () => {
    test.each([
        [
            "insert at the end",
            ["1", "2", "3"],
            "4",
            null,
            ["1", "2", "3", "4"]
        ],
        [
            "insert into the middle",
            ["1", "2", "3"],
            "4",
            "2",
            ["1", "4", "2", "3"]
        ],
        [
            "insert at the beginning",
            ["1", "2", "3"],
            "4",
            "1",
            ["4", "1", "2", "3"]
        ],
        [
            "wrong afterElement argument",
            ["1", "2", "3"],
            "4",
            "5",
            ["1", "2", "3", "4"]
        ]
    ])("%s", (name: string, elements: string[], element: string, afterElement: string, expected: string[]) => {
        expect(insertBefore(elements, element, afterElement)).toEqual(expected);
    });
});

