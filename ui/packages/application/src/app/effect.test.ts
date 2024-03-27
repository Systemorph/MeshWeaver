import { expect, test, describe, jest } from "@jest/globals";
import { Observable, of } from "rxjs";
import { effect } from "./effect";

// TODO: tbd (3/27/2024, akravets)
describe("effect", () => {
    test("1", () => {
        const subscription =
            new Observable(
                subscriber => {
                    subscriber.next(1);
                    subscriber.next(2);
                    subscriber.next(3);
                    subscriber.complete();
                })
                .pipe(effect(value => () => console.log("effect " + value)))
                .subscribe(console.log);

        subscription.unsubscribe();
    });
});
