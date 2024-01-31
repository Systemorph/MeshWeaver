import { NumericDictionary } from "lodash";

const MAX_HEADING_LEVEL = 6;

export function createNumbering(value?: string) {
    const dict: NumericDictionary<number> = value ? {...value.split('.').map(Number)} : {};

    function update(level: number) {
        for (let l = level + 1; l <= MAX_HEADING_LEVEL; l++) {
            if (dict[l] !== undefined) {
                dict[l] = undefined;
            }
        }

        if (dict[level] === undefined) {
            dict[level] = 1;
        }
        else {
            dict[level] += 1;
        }

        return dict;
    }

    return function next(level: number) {
        update(level);

        let number = '';

        for (let j = 1; j <= level; j++) {
            number += (dict[j] === undefined ? '0' : dict[j]) + '.';
        }

        return number.slice(0, -1);
    }
}


