import { AgGridReact } from "ag-grid-react";
import { useState } from "react";
import { Bill, dateFormat } from "./billingApi";
import { Link } from "react-router-dom";
import { formatDate } from "@open-smc/utils/src/numbers";
import { ColDef } from "ag-grid-community";
import { useProject } from "../project/projectStore/hooks/useProject";
import { formatAmount } from "./billingUtils";
import styles from "./billing-history.module.scss";
import classNames from "classnames";

interface Props {
    bills: Bill[];
}

export function BillingHistory({bills}: Props) {
    const {project} = useProject();

    const [columnDefs] = useState<ColDef[]>([
        {
            headerName: 'Period',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '14px',
                lineHeight: '16px',
                minHeight: '48px',
                rowHeight: '48px',
            },
            width: 273,
            cellRendererFramework: ({data}: { data: Bill }) => {
                const {periodStart, periodEnd} = data;
                return <span>{formatDate(periodStart, dateFormat)} to {formatDate(periodEnd, dateFormat)}</span>;
            },
        },
        {
            headerName: 'Date',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '14px',
                lineHeight: '16px',
                minHeight: '48px',
                rowHeight: '48px',
            },
            field: 'billDate',
            cellRendererFramework: ({data}: { data: Bill }) => {
                return formatDate(data.billDate, dateFormat);
            },
            width: 105,
        },
        {
            field: 'totalAmount',
            headerClass: 'align-right',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'flex-end',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '14px',
                fontWeight: '700',
                lineHeight: '16px',
                minHeight: '48px',
                rowHeight: '48px',
            },
            valueFormatter: ({value}) => formatAmount(value),
            width: 141,
        },
        {
            headerName: 'Status',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '48px',
                rowHeight: '48px',
            },

            width: 85,
            cellRendererFramework: ({data}: { data: Bill }) => {
                return data.status === 'Paid' ? <span className={classNames(styles.status, styles.paid)}>Paid</span> :
                    <span className={classNames(styles.status, styles.open)}>Open</span>;
            },
        },
        {
            headerName: 'Invoice',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '14px',
                lineHeight: '16px',
                minHeight: '48px',
                rowHeight: '48px',
            },
            width: 87,
            cellRendererFramework: ({data}: { data: Bill }) => {
                return (
                    <Link className={styles.invoice} to={`/project/${project.id}/invoice/${data.id}`}>Invoice</Link>
                );
            },
        },
        {
            headerName: 'Details',
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '48px',
                rowHeight: '48px',
            },
            width: 87,
            autoHeight: true,
            cellRendererFramework: ({data}: { data: Bill }) => {
                return (
                    <Link className={styles.details} to={`bill/${data.id}`}>Details</Link>
                );
            },
        },
    ]);

    return (
        <div className="ag-theme-alpine" style={{height: 400, maxWidth: 778, overflowY: "auto"}}>
            <AgGridReact
                rowData={bills}
                columnDefs={columnDefs}
                domLayout="autoHeight"
            />
        </div>
    );
}

