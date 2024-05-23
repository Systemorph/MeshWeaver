import { expect, test, describe, jest } from "@jest/globals";
import { Binding } from "@open-smc/data/src/contract/Binding";
import { expandBindings } from "./expandBindings";

describe("expandBindings", () => {
    test("binding to value", () => {
        const data = {
            name: "foo",
            title: new Binding("$")
        }

        const dataContext = "bar";

        expect(expandBindings(data)(dataContext)).toEqual({
            name: "foo",
            title: "bar"
        });
    });

    test("binding to number", () => {
        const data = {
            name: "foo",
            age: new Binding("$")
        }

        const dataContext = 0;

        expect(expandBindings(data)(dataContext)).toEqual({
            name: "foo",
            age: 0
        });
    });

    test("binding to missing value", () => {
        const data = {
            name: "foo",
            age: new Binding("$.missing.path")
        }

        const dataContext = "";

        expect(expandBindings(data)(dataContext)).toEqual({
            name: "foo",
            age: null
        });
    });
});
