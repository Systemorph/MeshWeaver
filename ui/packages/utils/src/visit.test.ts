import { expect, test } from "@jest/globals";
import { PropertyPath, visit } from "./visit";

describe("visit", () => {
    test("basic", () => {
        const data = {
            $scopeId: "1",
            name: "data",
            category: {
                $scopeId: "2",
                name: "category",
                subcategory: {
                    $scopeId: "4",
                    name: "sub"
                }
            },
            test: {
                name: "plain"
            },
            array: [
                {
                    name: "plain"
                },
                {
                    $scopeId: "3",
                    name: "array element"
                }
            ]
        };

        const foundScopes: [string, PropertyPath][] = [];

        visit(data, (node: any, path) => {
            if (node.$scopeId) {
                foundScopes.push([node.$scopeId, path]);
            }
        })

        expect(foundScopes).toEqual([
            ["1", []],
            ["2", ["category"]],
            ["4", ["category", "subcategory"]],
            ["3", ["array", "1"]]
        ]);
    })
});
