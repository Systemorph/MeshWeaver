import { AgGridReact } from "ag-grid-react";
import { useState } from "react";
import { ColDef } from "ag-grid-community";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { AddUserForm } from "./AddUserForm";
import Tooltip from "rc-tooltip";
import { AccessGroup, GroupChangeToggle, GroupMember } from "./accessControl.contract";
import { Link } from "react-router-dom";
import classNames from "classnames";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import user from "./users.module.scss";
import avatar from '../../shared/components/avatar.module.scss';
import styles from "./group.module.scss";
import { getInitials } from "../../shared/utils/getInitials";
import accessButton from "./access-control-buttons.module.scss";

type OnChange = (accessObject: string, toggle: GroupChangeToggle) => void;

interface Props {
    group: AccessGroup;
    memberships: GroupMember[];
    editable: boolean;
    onChange: OnChange;
    canOverride?: boolean;
    loading?: boolean;
}

export function Group({group, memberships, editable, onChange, canOverride, loading}: Props) {
    const columnDefs: ColDef[] = [
        {
            headerName: 'User',
            cellRendererFramework: ({data}: {data: GroupMember}) => {
                const initials = data.displayName ? getInitials(data.displayName) : data.accessObject.substring(0, 1);
                return (
                    <div className={user.container} data-qa-user>
                        {data.toggle === 'Remove' && <i className={classNames(user.lockIcon, 'sm sm-lock')}/>}
                        <div className={avatar.userPic}>{initials}</div>
                        <div className={user.nameContainer}>
                            <Link data-qa-link className={user.mail} to={`../users/${data.accessObject}`}>
                                <span data-qa-name className={user.name}>{data.displayName}</span>
                                <span data-qa-email>{data.accessObject}</span>
                            </Link>
                        </div>
                    </div>
                )
            },
            flex: 1,
            cellStyle: {
                display: 'flex',
                alignItems: 'center',
                paddingTop: '8px',
                paddingBottom: '8px'
            },
            autoHeight: true,
        },
        {
            headerName: 'Action',
            headerClass: 'align-right',
            cellRendererFramework: ({data}: {data: GroupMember}) => {
                return (
                    <div className={styles.buttonsContainer}>
                        {canOverride && !data.inherited &&
                            <Button
                                icon="sm sm-undo"
                                data-qa-btn-restore
                                className={classNames(button.blankButton, button.button)}
                                onClick={() => onChange(data.accessObject, 'Inherit')}
                                label="Restore"
                                disabled={!editable}
                            />
                        }
                        {data.toggle === 'Add'
                            ? <Button
                                icon="sm sm-lock"
                                data-qa-btn-deny
                                className={classNames(accessButton.access, accessButton.deny, button.button)}
                                onClick={() => onChange(data.accessObject, 'Remove')}
                                label="Deny"
                                disabled={!editable}/>
                            :
                            <Button
                                icon="sm sm-check"
                                data-qa-btn-allow
                                className={classNames(accessButton.access, accessButton.allow, button.button)}
                                onClick={() => onChange(data.accessObject, 'Add')}
                                label="Allow"
                                disabled={!editable}/>
                        }
                    </div>
                );
            },
            width: 200,
            cellStyle: {
                display: 'flex',
                justifyContent: 'flex-end',
                paddingTop: '12px',
                paddingBottom: '12px',
            },
            autoHeight: true,
        }
    ];

    return (
        <div className={styles.container} data-qa-group>
            <h3 className={styles.title}><Link className={styles.groupsLink} to={'..'}>All
                groups / </Link>{group.displayName}</h3>
            <div className={styles.descriptionContainer}>
                <i className={classNames(styles.descriptionIcon, "group-icon", "group-icon-big", "group-icon-" + group.name)}/>
                <div className={styles.descriptionContent}>
                    <h2 className={styles.descriptionName} data-qa-group-name>{group.displayName}</h2>
                    <p className={styles.descriptionText}>{group.description}</p>
                </div>

            </div>
            <div className={styles.membershipsContainer}>
                <h3 className={styles.memberships}>Users <span
                    className={styles.membershipsNumber} data-qa-user-amount>{memberships.length}</span></h3>
                <AddUser
                    addToGroup={group}
                    onChange={onChange}
                    disabled={!editable || loading}
                />
            </div>
            <div className={classNames({loading})}>
                <div data-qa-user-list className="ag-theme-alpine" style={{height: 600, maxWidth: 974}}>
                    <AgGridReact
                        rowData={memberships}
                        columnDefs={columnDefs}
                        getRowClass={({data}: {data: GroupMember}) => data.inherited ? 'inherited' : undefined}
                    />
                </div>
            </div>
        </div>
    );
}

interface AddUserProps {
    addToGroup: AccessGroup;
    onChange: OnChange;
    disabled: boolean;
}

const getPopupContainer = () => document.getElementById('scrollable');

function AddUser({addToGroup, onChange, disabled}: AddUserProps) {
    const [formVisible, setFormVisible] = useState(false);

    const className = classNames(button.button, button.primaryButton, styles.addButton, {active: formVisible});

    return (
        <Tooltip
            overlayClassName={styles.groupTooltip}
            visible={formVisible}
            onVisibleChange={setFormVisible}
            placement="bottomRight"
            trigger="click"
            getTooltipContainer={getPopupContainer}
            overlay={formVisible &&
                <AddUserForm
                    addToGroup={addToGroup}
                    onSubmit={({user, toggle}) => {
                        onChange(user, toggle);
                        setFormVisible(false);
                    }}
                    onCancel={() => setFormVisible(false)}/>
            }
        >
            <Button data-qa-btn-add-user
                    className={className}
                    disabled={disabled}
                    icon="sm sm-plus"
                    label="Add user"/>
        </Tooltip>
    )
}