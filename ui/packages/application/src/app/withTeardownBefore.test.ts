import { expect, test, describe, jest } from "@jest/globals";
import { Observable, of } from "rxjs";
import { withTeardownBefore } from "./withTeardownBefore";

describe("basic", () => {
    test("1", () => {
        new Observable(subscriber => subscriber.next(1))
            .pipe(withTeardownBefore(() => console.log("teardown")))
            .subscribe(console.log);
    });
});
