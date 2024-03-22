import { expect, test, describe, jest } from "@jest/globals";
import { Observable, of } from "rxjs";
import { withCleanup } from "./withCleanup";

describe("cleanup", () => {
    test("1", () => {
        const subscription =
            new Observable(
                subscriber => {
                    subscriber.next(1);
                    subscriber.next(2);
                    subscriber.next(3);
                    subscriber.complete();
                })
                .pipe(withCleanup(value => () => console.log("cleanup " + value)))
                .subscribe(console.log);

        // subscription.unsubscribe();
    });
});
