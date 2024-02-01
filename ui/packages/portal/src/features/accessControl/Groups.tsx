import {AgGridReact} from "ag-grid-react";
import {useState} from "react";
import {Link} from "react-router-dom";
import styles from "./groupBadge.module.scss";
import classNames from "classnames";
import {AccessGroup} from "./accessControl.contract";

interface GroupsProps {
    groups: AccessGroup[];
}

export function Groups({groups}: GroupsProps) {
    const [columnDefs] = useState([
        {
            headerName: 'Group',
            field: 'displayName',
            cellRendererFramework: ({data}: { data: AccessGroup }) => {
                return (
                    <Link className={styles.link} to={data.name} data-qa-name={data.displayName} data-qa-link>
                        <i className={classNames("group-icon", "group-icon-" + data.name)}/>{data.displayName}
                    </Link>
                );
            },
            width: 200,
            cellStyle: {
                display: 'flex',
                alignItems: 'top',
                paddingTop: '16px',
                paddingBottom: '16px',
                rowHeight: '64px',
                minHeight: '64px'
            },
        },
        {
            field: 'description',
            flex: 1,
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                fontSize: '12px',
                paddingTop: '24px',
                paddingBottom: '24px',
                lineHeight: '16px',
                minHeight: '64px'
            },
            autoHeight: true,
            wrapText: true
        }
    ]);

    return (
        <div data-qa-ac-groups className="ag-theme-alpine" style={{height: 400, maxWidth: 800}}>
            <AgGridReact
                data-qa-group-list
                rowData={groups}
                columnDefs={columnDefs}
                domLayout="autoHeight"
            />
        </div>
    );
}
