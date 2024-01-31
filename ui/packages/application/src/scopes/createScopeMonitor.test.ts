import { expect, test, it, jest } from "@jest/globals";
import { createScopeMonitor } from "./createScopeMonitor";

describe("basic tests", () => {
    test("one instance", () => {
        const data = {
            a: {
                $scopeId: "a",
                name: "A"
            },
            b: {
                $scopeId: "b",
                name: "B"
            }
        };

        const nextFn = jest.fn();

        const setProperty = createScopeMonitor(data, nextFn);

        setProperty("a", "name", "AA");

        expect(nextFn).toHaveBeenCalledTimes(1);

        const [nextData] = nextFn.mock.lastCall as [typeof data];

        expect(nextData.a).toEqual({
            $scopeId: "a",
            name: "AA"
        });

        expect(nextData).not.toBe(data);

        expect(nextData.a).not.toBe(data.a);

        expect(nextData.b).toBe(data.b);
    });

    test("multiple instances", () => {
        const data = {
            a: {
                $scopeId: "a",
                name: "A"
            },
            nested: {
                a: {
                    $scopeId: "a",
                    name: "A"
                },
                b: {
                    $scopeId: "b",
                    name: "B"
                }
            }
        };

        const nextFn = jest.fn();

        const setProperty = createScopeMonitor(data, nextFn);

        setProperty("a", "name", "AA");

        expect(nextFn).toHaveBeenCalledTimes(1);

        const [nextData] = nextFn.mock.lastCall as [typeof data];

        expect(nextData.a).toEqual({
            $scopeId: "a",
            name: "AA"
        });

        expect(nextData.nested.a).toEqual(nextData.a);

        expect(nextData.nested.b).toBe(data.nested.b);
    });

    test("array element", () => {
        const data = {
            a: {
                $scopeId: "a",
                name: "A"
            },
            b: {
                $scopeId: "b",
                name: "B"
            },
            scopes: [
                {
                    $scopeId: "b",
                    name: "B"
                },
                {
                    $scopeId: "c",
                    name: "C"
                }
            ]
        };

        const nextFn = jest.fn();

        const setProperty = createScopeMonitor(data, nextFn);

        setProperty("c", "name", "CC");

        expect(nextFn).toHaveBeenCalledTimes(1);

        const [nextData] = nextFn.mock.lastCall as [typeof data];

        expect(nextData).not.toBe(data);

        expect(nextData.a).toBe(data.a);

        expect(nextData.b).toBe(data.b);

        expect(nextData.scopes).not.toBe(data.scopes);

        expect(nextData.scopes[0]).toBe(data.scopes[0]);

        expect(nextData.scopes[1]).toEqual({
            $scopeId: "c",
            name: "CC"
        });
    });
});
