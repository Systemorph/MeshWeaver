import { expect, test } from "@jest/globals";
import { makeBinding } from "./resolveBinding";
import { makeDataContext } from "./DataContextBuilder";

describe("basic", () => {
    test("object property", () => {
        const dataContext = makeDataContext({
            name: "A"
        }).build();

        expect(dataContext.resolveBinding("name").value).toBe("A");
    });

    test("nested object property", () => {
        const dataContext = makeDataContext({
            user: {
                category: {
                    id: "1"
                }
            }
        }).build();

        expect(dataContext.resolveBinding("user.category.id").value).toBe("1");
    });

    test("array element", () => {
        const dataContext = makeDataContext({
            users: [
                null,
                {
                    category: {
                        id: "1"
                    }
                }
            ]
        }).build();

        expect(dataContext.resolveBinding("users[1].category.id").value).toBe("1");
    });

    test("missing property", () => {
        const dataContext = makeDataContext("abc").build();

        expect(dataContext.resolveBinding("name").value).toBe(undefined);
    });

    test("binding to context itself", () => {
        const dataContext = makeDataContext("test").build();

        expect(dataContext.resolveBinding("").value).toBe("test");
    });
});

describe("inheritance", () => {
    test("simple binding to upper level", () => {
        const dataContext0 = makeDataContext({
            users: [
                {
                    name: "Bob"
                }
            ]
        }).build();

        const dataContext1 = makeDataContext({
            user: makeBinding("users[0]")
        }).withParentContext(dataContext0).build();

        expect(dataContext1.resolveBinding("user.name").value).toBe("Bob");
    });

    test("skipping one level", () => {
        const dataContext0 = makeDataContext({
            users: [
                {
                    name: "Bob"
                }
            ]
        }).build();

        const dataContext1 = makeDataContext({}).withParentContext(dataContext0).build();

        const dataContext2 = makeDataContext({
            user: makeBinding("users[0]")
        }).withParentContext(dataContext1).build();

        expect(dataContext2.resolveBinding("user.name").value).toBe("Bob");
    });

    test("binding in the middle", () => {
        const dataContext0 = makeDataContext({
            users: [
                {
                    name: "Bob"
                }
            ]
        }).build();

        const dataContext1 = makeDataContext({
            users: null as any,
            scope: {
                user: makeBinding("users[0]")
            }
        }).withParentContext(dataContext0).build();

        const dataContext2 = makeDataContext({
            userName: makeBinding("scope.user.name")
        }).withParentContext(dataContext1).build();

        expect(dataContext2.resolveBinding("userName").value).toBe("Bob");
    });

    test("undefined", () => {
        const dataContext0 = makeDataContext({
            users: [
                {
                    name: "Bob"
                }
            ]
        }).build();

        const dataContext1 = makeDataContext(undefined).withParentContext(dataContext0).build();

        expect(dataContext1.resolveBinding("users[0].name").value).toBe("Bob");
    });
});