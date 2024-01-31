import { getInitials } from "./getInitials";
import {expect, jest, test} from '@jest/globals';

describe("test generated initials", () => {
    test.each([
        ['Albus Percival Wulfric Brian dumbledore', 'AD'],
        ['Harry Potter', 'HP'],
        ['Ron', 'R'],
        ['', ''],
        ['Çigkofte With Érnie', 'ÇÉ'],
        ['Hermione ', 'H'],
        ['Neville LongBottom ', 'NL'],
        ['Aleksandr Vinokruov', 'AV']
      ])(".test %s %s", (a: string, expected: string) => {
        expect(getInitials(a)).toBe(expected);
    });
});

