import { getUniqueFileName } from "./getUniqueFileName";
import {expect, jest, test} from '@jest/globals';

describe("test new generated names", () => {
    test.each([
        [
            "New notebook",
            ["New notebook", "New notebook (2)"],
            "New notebook (3)",
        ],
        ["test", ["test", "1.txt", "test2.txt"], "test (2)"],
        [
            "test.txt",
            ["test.txt", "test (2).txt", "test (4).txt"],
            "test (3).txt",
        ],
        [
            "test.txt",
            ["test.txt", "test (10).txt", "test (2).txt", "test (3).txt", "test (4).txt", "test (5).txt", "test (6).txt", "test (7).txt", "test (8).txt", "test (9).txt"],
            "test (11).txt",
        ],
        [
            "New notebook",
            ["New notebook", "New notebook (10)", "New notebook (2)", "New notebook (3)", "New notebook (4)", "New notebook (5)", "New notebook (6)", "New notebook (7)", "New notebook (8)", "New notebook (9)"],
            "New notebook (11)",
        ],
        ['test.txt', ['test.png'], 'test.txt'],
        ['test.txt', ['test'], 'test.txt'],
        ['test.txt', ['test.txt.png'], 'test.txt'],
        ['my.file.txt', ['my.file.txt'], 'my.file (2).txt'],
        ['.gitignore', ['.gitignore', '.gitignore (2)'], '.gitignore (3)'],
        ['.file.txt', ['.file.txt'], '.file (2).txt'],
        ['file.txt', ['file (0).txt'], 'file.txt'],
        ['test', ['test', 'test (02)'], 'test (2)'],
        ['test', ['test', 'test (0)', 'test (01)'], 'test (2)'],
        ['test', ['test (2)', 'test (3)'], 'test'],
        ['test', ['TEST', 'test (2)'], 'test (3)'],
        ['TEST', ['TEST', 'test (2)'], 'TEST (3)'],
        ['TEST.md', ['TEST.MD', 'test (2).md'], 'TEST (3).md'],
        ['TEST.MD', ['TEST.md', 'test (2).md'], 'TEST (3).MD'],
    ])(".test %s %s", (a: string, b: string[], expected: string) => {
        expect(getUniqueFileName(a, b)).toBe(expected);
    });
});

