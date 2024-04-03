import { expect, test, describe, jest } from "@jest/globals";
import { selectByReference } from "./selectByReference";

import { JsonPathReference } from "./contract/JsonPathReference";

describe("selectByReference", () => {
    test("data is string", () => {
        expect(selectByReference("", new JsonPathReference("$"))).toBe("");
        expect(selectByReference("foo", new JsonPathReference("$"))).toBe("foo");
    });

    test("data is number", () => {
        expect(selectByReference(0, new JsonPathReference("$"))).toBe(0);
        expect(selectByReference(42, new JsonPathReference("$"))).toBe(42);
    });

    test("value is string", () => {
        expect(selectByReference({ name: "foo" }, new JsonPathReference("$.name"))).toBe("foo");
    });

    test("value is number", () => {
        expect(selectByReference({ age: 0 }, new JsonPathReference("$.age"))).toBe(0);
        expect(selectByReference({ age: 42 }, new JsonPathReference("$.age"))).toBe(42);
    });
});
