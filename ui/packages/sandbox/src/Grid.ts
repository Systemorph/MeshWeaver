import type { GridView } from "@open-smc/application/src/controls/GridControl";
import type { GridOptions } from 'ag-grid-community';
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Grid extends ControlBase implements GridView {
    constructor(public data: GridOptions) {
        super("GridControl");
    }
}

export class GridBuilder extends ControlBuilderBase<Grid> {
    constructor() {
        super(Grid);
    }

    withOptions(options: GridOptions) {
        this.data.data = options;
        return this;
    }
}

export const makeGrid = () => new GridBuilder();