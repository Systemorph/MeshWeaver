import { expect, test, describe, jest } from "@jest/globals";
import { Observable, of } from "rxjs";
import { effect } from "./effect";

// TODO: write proper test (3/27/2024, akravets)
describe("effect", () => {
    test("1", () => {
        const subscription =
            of(1, 2, 3)
                .pipe(effect(sample("1")))
                .pipe(effect(sample("2")))
                .subscribe();

        // subscription.unsubscribe();
    });
});

const sample = (id: string) =>
    (value: number) => {
        console.log(`effect id = ${id}, value=${value}`);
        return () => console.log(`cleanup id = ${id}, value=${value}`);
    }

