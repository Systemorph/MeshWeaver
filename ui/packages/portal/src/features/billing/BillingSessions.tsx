import { Bill, dateTimeFormat } from "./billingApi";
import { AgGridReact } from "ag-grid-react";
import { useRef, useState } from "react";
import { formatNumber } from "@open-smc/utils/src/numbers";
import { formatDate } from "@open-smc/utils/src/formatDate";
import { ColDef } from "ag-grid-community";
import { SessionDescriptor } from "../../app/notebookFormat";
import { formatCredits } from "./billingUtils";
import styles from "./billing-overview.module.scss";
import { useIncrement } from "@open-smc/utils/src/useIncrement";
import classNames from "classnames";
import { Page } from "../../shared/utils/requestWrapper";

interface Props {
    bill: Bill;
    sessions: Page<SessionDescriptor>;
    printLayout?: boolean;
}

export function BillingSessions({bill, sessions, printLayout}: Props) {
    const [_, update] = useIncrement();
    const {id, subscription, totalCreditsUsed, extraCredits, extraCreditAmount, totalAmount, status} = bill;
    const {subscriptionFee, billingPeriod, includedCredits, pricePerAdditionalCredit} = subscription.details;

    const topGrid = useRef(null);

    const [colDefs] = useState<ColDef[]>([
        {
            headerName: 'Session Id',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: 'auto',
            },
            // field: 'id',
            width: 154,
            wrapText: true,
            autoHeight: true,
            colSpan: ({data}: any) => data?.footer ? 9 : 1,
            cellRendererFramework: ({data}: any) => data?.footer ? data.label : data.id
        },
        {
            headerName: 'User',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            width: 205,
            wrapText: true,
            // autoHeight: true,
            cellRendererFramework: ({data}: { data: SessionDescriptor }) => {
                return (
                    <span>{data.startedBy}</span>
                );
            },
        },
        {
            headerName: 'Start',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            field: 'started',
            valueFormatter: ({value}) => value && formatDate(value, dateTimeFormat),
            width: 114,
            wrapText: true,
        },
        {
            headerName: 'End',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            field: 'stopped',
            valueFormatter: ({value}) => value && formatDate(value, dateTimeFormat),
            width: 114,
            wrapText: true,
        },
        {
            headerName: 'Series',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            valueGetter: ({data}: { data: SessionDescriptor }) => data.specification?.tier,
            width: 65,
        },
        {
            headerName: 'CPU(s)',
            headerClass: 'align-right',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'flex-end',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            valueGetter: ({data}: { data: SessionDescriptor }) => data.specification?.cpu,
            width: 63,
        },
        {
            headerName: 'RAM',
            headerClass: 'align-right',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'flex-end',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            valueGetter: ({data}: { data: SessionDescriptor }) => data.specification?.memory,
            width: 63,
        },
        {
            headerName: 'Credits/Min',
            headerClass: 'align-end',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'flex-end',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            valueGetter: ({data}: { data: SessionDescriptor }) => data.specification?.creditsPerMinute,
            width: 101,
        },
        {
            headerName: 'Duration Min',
            headerClass: 'align-end',
            headerComponentParams: {
                template: '<span>Duration,<br> Min</span>>'
            },
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'flex-end',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            field: 'duration',
            valueFormatter: ({value}) => formatNumber(value, '0.00'),
            width: 87,
        },
        {
            headerName: 'Credits/Session',
            headerClass: 'align-end',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'flex-end',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '49px',
                rowHeight: '49px',
            },
            // field: 'creditsUsed',
            // valueFormatter: ({value}) => formatCredits(value),
            width: 131,
            // flex: 1,
            // autoHeight: printLayout,
            cellRendererFramework: ({data}: any) => data.footer ? data.value : formatCredits(data.creditsUsed)
        },
    ]);

    const [bottomData] = useState([
        {
            footer: true,
            label: (
                <div style={{display: 'flex', justifyContent: "space-between", width: '934px'}}>
                    <div style={{}}>Total
                        sessions: {sessions.totalCount}</div>
                    <div style={{fontWeight: 'bold'}}>Total
                        credits spent
                    </div>
                </div>
            ),
            value: <span className={styles.value}>{formatCredits(totalCreditsUsed)}</span>
        },
        {
            footer: true,
            label: <div style={{display: 'flex', justifyContent: "flex-end", width: '934px'}}>Included
                credits</div>,
            value: <span className={styles.value}>{formatCredits(includedCredits)}</span>
        },
        {
            footer: true,
            label: <div style={{display: 'flex', justifyContent: "flex-end", width: '934px'}}>Extra credits</div>,
            value: <span className={classNames(styles.value, styles.valueCredits)}>{formatCredits(extraCredits)}</span>
        },
    ]);

    const autoHeight = printLayout || sessions.rows.length < 5;

    return (
        <div>
            <div style={{
                maxWidth: 1098,
                display: 'flex',
                flexDirection: 'column'
            }}>
                <div style={{
                    flex: "1 1 auto",
                    height: !autoHeight ? 400 : undefined
                }} className="ag-theme-alpine">
                    <AgGridReact
                        ref={topGrid}
                        rowData={sessions.rows}
                        columnDefs={colDefs}
                        domLayout={autoHeight ? 'autoHeight' : 'normal'}
                        suppressHorizontalScroll
                        onGridReady={update}
                        //suppressMovable={true}
                    />
                </div>
                <div style={{flex: "1 1 auto"}} className={classNames("ag-theme-alpine", styles.bottomGrid)}>
                    <AgGridReact
                        alignedGrids={topGrid.current ? [topGrid.current] : undefined}
                        rowData={bottomData}
                        columnDefs={colDefs}
                        headerHeight={0}
                        domLayout={'autoHeight'}
                        // suppressMovable={true}
                    />
                </div>
            </div>
            <p className={styles.text}>All amounts shown are in CHF.</p>
        </div>
    );
}