import {AgGridReact} from "ag-grid-react";
import {useState} from "react";
import {Link} from "react-router-dom";
import styles from "./users.module.scss";
import avatar from '../../shared/components/avatar.module.scss';
import {AccessObject, AccessUser} from "./accessControl.contract";
import { getInitials } from "../../shared/utils/getInitials";

interface UsersProps {
    users: AccessObject[];
}

export function Users({users}: UsersProps) {
    const [columnDefs] = useState([
        {
            field: 'name',
            headerName: 'User',
            cellRendererFramework: ({data}: { data: AccessUser }) => {
                const initials = data.displayName ? getInitials(data.displayName) : data.name.substring(0, 1);
                return (
                    <div className={styles.container} data-qa-user>
                        <div className={avatar.userPic}>{initials}</div>
                        <div className={styles.nameContainer}>
                            <Link data-qa-link className={styles.mail} to={data.name}>
                                <span className={styles.name} data-qa-name>{data.displayName}</span>
                                <span data-qa-email>{data.name}</span>
                            </Link>
                        </div>
                    </div>
                )
            },
            flex: 1,
            autoHeight: true,
            cellStyle: {
                display: 'flex',
                alignItems: 'top',
                paddingTop: '8px',
                paddingBottom: '16px',
                paddingRight: '16px',
            },
        },
        // { field: 'groups' },
    ]);

    return (
        <div data-qa-ac-users className="ag-theme-alpine" style={{height: 600, maxWidth: 800}}>
            <AgGridReact
                data-qa-user-list
                rowData={users}
                columnDefs={columnDefs}
                getRowClass={({data}: { data: AccessUser }) => data.inherited ? 'inherited' : undefined}
            />
        </div>
    );
}
