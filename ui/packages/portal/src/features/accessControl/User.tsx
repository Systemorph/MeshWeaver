import { AgGridReact, AgGridColumn } from "ag-grid-react";
import { useState } from "react";
import { map } from "lodash";
import { ColDef } from "ag-grid-community/dist/lib/entities/colDef";
import { AccessGroup, AccessMembership, AccessUser, GroupChangeToggle, UserMembership } from "./accessControl.contract";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import Tooltip from "rc-tooltip";
import { AddGroupForm } from "./AddGroupForm";
import { Link } from "react-router-dom";
import classNames from "classnames";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss";
import accessButton from "./access-control-buttons.module.scss";
import styles from "./user.module.scss";
import badge from "./groupBadge.module.scss";

type OnChange = (memberOf: string, toggle: GroupChangeToggle) => void;

interface Props {
    user: AccessUser;
    memberships: UserMembership[];
    groups: AccessGroup[];
    editable: boolean;
    onChangeMembership: OnChange;
    canOverride?: boolean;
    loading?: boolean;
}

export function User({user, memberships, groups, editable, onChangeMembership, canOverride, loading}: Props) {
    const currentGroups = map(memberships, 'memberOf');
    const remainingGroups = groups.filter(g => currentGroups.indexOf(g.name) === -1);

    const columnDefs: ColDef[] = [
        {
            headerName: 'Group',
            cellRendererFramework: ({data}: {data: UserMembership}) => {
                return (
                    <Link className={badge.link} to={`../groups/${data.memberOf}`}>
                        <i className={classNames("group-icon", "group-icon-" + data.memberOf)}/>{data.displayName}
                    </Link>
                );
            },
            field: 'displayName',
            width: 200,
            cellStyle: {
                display: 'flex',
                alignItems: 'start',
                paddingTop: '16px',
                paddingBottom: '16px',
                rowHeight: '64px'
            },
        },
        {
            headerName: 'Description',
            field: 'description',
            flex: 1,

            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '16px',
                paddingBottom: '16px',
                fontSize: '12px',
                lineHeight: '16px',
                minHeight: '64px'
            },
            autoHeight: true,
            wrapText: true
        },
        {
            headerName: 'Action',
            headerClass: 'align-right',
            cellStyle: {
                display: 'flex',
                justifyContent: 'flex-end',
                paddingTop: '16px',
                paddingBottom: '16px',
                rowHeight: '64px',
                minHeight: '64px'
            },
            cellRendererFramework: ({data}: {data: AccessMembership}) => {
                return (
                    <div className={styles.buttonsContainer}>
                        {canOverride && !data.inherited &&
                            <Button
                                icon="sm sm-undo"
                                data-qa-btn-restore
                                className={classNames(button.blankButton, button.button)}
                                onClick={() => onChangeMembership(data.accessObject, 'Inherit')}
                                label="Restore"
                                disabled={!editable}
                            />
                        }
                        {data.toggle === 'Add'
                            ? <Button
                                icon="sm sm-lock"
                                data-qa-btn-deny
                                className={classNames(accessButton.access, accessButton.deny, button.button)}
                                onClick={() => onChangeMembership(data.memberOf, 'Remove')}
                                label="Deny"
                                disabled={!editable}/>
                            :
                            <Button
                                icon="sm sm-check"
                                data-qa-btn-allow
                                className={classNames(accessButton.access, accessButton.allow, button.button)}
                                onClick={() => onChangeMembership(data.memberOf, 'Add')}
                                label="Allow"
                                disabled={!editable}/>
                        }
                    </div>
                );
            },
            width: 200
        }
    ];

    return (
        <div className={styles.container} data-qa-user>
            <h2 className={styles.title}>
                <Link className={styles.usersLink} to={'../users'}>All users</Link> / <span
                data-qa-email>{user.name}</span></h2>
            <div className={styles.membershipsContainer}>
                <h3 className={styles.memberships}>Manage groups
                    <span className={styles.membershipsNumber} data-qa-user-amount> {memberships.length}</span>
                </h3>
                <AddGroup
                    user={user}
                    groups={remainingGroups}
                    onChangeMembership={onChangeMembership}
                    disabled={!editable || remainingGroups.length === 0 || loading}
                />
            </div>
            <div className={classNames({loading})}>
                <div className="ag-theme-alpine" style={{height: 600, maxWidth: 974}}>
                    <AgGridReact
                        data-qa-group-list
                        rowData={memberships}
                        columnDefs={columnDefs}
                        domLayout="autoHeight"
                    />
                </div>
            </div>
        </div>
    );
}

interface AddGroupProps {
    user: AccessUser;
    groups: AccessGroup[];
    onChangeMembership: OnChange;
    disabled?: boolean;
}

const getPopupContainer = () => document.getElementById('scrollable');

function AddGroup({user, groups, onChangeMembership, disabled}: AddGroupProps) {
    const [formVisible, setFormVisible] = useState(false);

    const className = classNames(button.primaryButton, button.button, styles.addButton, {active: formVisible});

    return (
        <Tooltip
            overlayClassName={styles.userTooltip}
            id="select-container"
            visible={formVisible}
            onVisibleChange={setFormVisible}
            placement="bottomRight"
            trigger="click"
            getTooltipContainer={getPopupContainer}
            overlay={formVisible &&
                <AddGroupForm
                    user={user}
                    groups={groups}
                    onSubmit={({group, toggle}) => {
                        onChangeMembership(group.name, toggle);
                        setFormVisible(false);
                    }}
                    onCancel={() => setFormVisible(false)}/>
            }
        >
            <Button data-qa-btn-add-group
                    className={className}
                    disabled={disabled}
                    icon="sm sm-plus"
                    label="Add group"/>
        </Tooltip>
    )
}