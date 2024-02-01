import { Subject } from "rxjs";
import { Hub } from "./Hub";
import { jest } from "@jest/globals";

describe("basic", () => {
    test("exposure", () => {
        const inner = new Hub();

        const outer = inner.exposeAs(new Hub());

        const outerSubscriber = jest.fn();
        const innerSubscriber = jest.fn();

        outer.subscribe(outerSubscriber);
        inner.subscribe(innerSubscriber);

        inner.next("1");
        outer.next("2");

        expect(outerSubscriber).toHaveBeenCalledWith("1");
        expect(innerSubscriber).toHaveBeenCalledWith("2");
    });
});