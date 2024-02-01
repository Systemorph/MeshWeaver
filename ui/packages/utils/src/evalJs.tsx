import { isArray, isObjectLike, isString } from "lodash";

export function evalJs(value: any, evalRegexps: (RegExp | string)[]) {
    const regexps = evalRegexps?.map(e => isString(e) ? new RegExp(e) : e);

    traverse(value);

    function traverse(value: any, parent?: any, key?: any): any {
        if (isArray(value)) {
            for (let i = 0; i < value.length; i++) {
                traverse(value[i], value, i);
            }
        } else if (isObjectLike(value)) {
            for (let key in value) {
                traverse(value[key], value, key);
            }
        } else {
            if (isString(value) && regexps?.some(regexp => regexp.test(value))) {
                eval(`parent[key] = ${value}`);
            } else {
                parent[key] = value;
            }
        }
    }
}

export const REGEXPS = {
    func: [/^function\b/, /^\(function\b/, /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/],
};