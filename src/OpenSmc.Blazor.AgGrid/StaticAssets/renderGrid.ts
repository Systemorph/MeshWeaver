import { GridOptions, GridApi, createGrid } from 'ag-grid-community';
import 'ag-grid-community/styles/ag-grid.css';
import "ag-grid-community/styles/ag-theme-quartz.css";
import 'ag-grid-enterprise';
import { cloneDeepWith, isString } from 'lodash-es';

const gridInstances = new Map<string, GridApi>();

export const renderGrid = (id: string, element: HTMLElement, options: GridOptions) => {
    destroyGrid(id);

    const clonedOptions = cloneDeepWith(options, value => {
        if (isString(value) && funcRegexps.some(regexp => regexp.test(value))) {
            try {
                return eval(`(${value})`);
            } catch (error) {
                console.error("Error evaluating function string:", error);
                return null;
            }
        }
    });

    gridInstances.set(id, createGrid(element, clonedOptions));
}

export const destroyGrid = (id: string) => 
    gridInstances.get(id)?.destroy();

const funcRegexps = [
    /^function\b/,
    /^\(function\b/,
    /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
];