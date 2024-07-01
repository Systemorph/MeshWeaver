import { GridOptions, GridApi, createGrid } from 'ag-grid-community';
import 'ag-grid-community/styles/ag-grid.css';
import "ag-grid-community/styles/ag-theme-alpine.css";
import 'ag-grid-enterprise';
import { cloneDeepWith, isString } from 'lodash-es';
export { LicenseManager } from 'ag-grid-enterprise';

const instances = new Map<string, GridApi>();

export const renderGrid = (id: string, element: HTMLElement, options: GridOptions) => {
    const instance = instances.get(id);
    
    if (instance) {
        instance.destroy();
        instances.delete(id);
    }

    const gridOptions = deserialize(options);

    instances.set(id, createGrid(element, gridOptions));
}

function deserialize<T>(data: T): T {
    return cloneDeepWith(data, value => {
        if (isString(value) && funcRegexps.some(regexp => regexp.test(value))) {
            try {
                return eval(`(${value})`);
            } catch (error) {
                console.error("Error evaluating function string:", error);
                return null;
            }
        }
    });
}

const funcRegexps = [
    /^function\b/,
    /^\(function\b/,
    /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
];