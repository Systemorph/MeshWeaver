import { ColDef, GridOptions } from 'ag-grid-community';
import 'ag-grid-enterprise';
import { AgGridReact } from 'ag-grid-react';
import 'ag-grid-community/styles/ag-grid.css';
import 'ag-grid-community/styles/ag-theme-alpine.css';
import { cloneDeep, cloneDeepWith, omit } from 'lodash';
import { evalJs, REGEXPS } from "@open-smc/utils/evalJs";
import { useEffect, useState } from "react";
import styles from "./gridControl.module.scss";
import { ControlView } from "../ControlDef";
import { LicenseManager } from "ag-grid-enterprise";

export interface GridView extends ControlView {
    data?: GridOptions;
}

LicenseManager.setLicenseKey(process.env.REACT_APP_OPEN_SMC_AG_GRID_LICENSE);

export default function GridControl({id, data: originalGridOptions, style}: GridView) {
    const [gridOptions, setGridOptions]
        = useState(makeGridOptions(originalGridOptions ?? {}));

    useEffect(() => {
        setGridOptions(makeGridOptions(originalGridOptions));
    }, [originalGridOptions]);

    const {rowData, columnDefs, ...options} = gridOptions;

    return (
        <div id={id} className={styles.theme} style={style}>
            <AgGridReact
                rowData={rowData}
                columnDefs={columnDefs}
                gridOptions={options}
                className={'ag-theme-alpine'}
            />
        </div>
    );
}

function makeGridOptions(originalGridOptions: GridOptions) {
    function customizer(value: any, key: string | number): any {
        if (value?.$type) {
            return cloneDeepWith(omit(value, "$type"), customizer)
        }
    }

    const gridOptions = cloneDeepWith(
        originalGridOptions,
        customizer
    );

    evalJs(gridOptions, REGEXPS.func);

    cleanupGridOptions(gridOptions);

    return gridOptions;
}

// TODO: delete once model is clean (11/29/2021, akravets)
function cleanupGridOptions(gridOptions: GridOptions) {
    delete (gridOptions as any).width;
    delete (gridOptions as any).height;

    gridOptions.columnDefs?.forEach((colDef: ColDef) => {
        delete (colDef as any).systemName;
        delete (colDef as any).displayName;
        delete (colDef as any).coordinates;
    });
}