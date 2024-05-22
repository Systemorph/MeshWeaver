import { expect, test, describe, jest } from "@jest/globals";
import { extractReferences } from "./extractReferences";

describe("extractReferences", () => {
    test("empty data should return empty array", () => {
        expect(extractReferences(null)).toEqual([]);
        expect(extractReferences(undefined)).toEqual([]);
    });
});
