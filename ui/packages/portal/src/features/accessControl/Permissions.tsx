import Switch from "rc-switch";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { AccessChangeToggle, AccessRestriction, Permission } from "./accessControl.contract";
import styles from "./permissions.module.scss";
import buttons from "@open-smc/ui-kit/src/components/buttons.module.scss"
import classNames from "classnames";

interface PermissionsProps {
    restrictions: AccessRestriction[];
    editable: boolean;
    canOverride?: boolean;
    onChange: (permission: Permission, toggle: AccessChangeToggle) => void;
    loading?: boolean;
}

export function Permissions({restrictions, editable, onChange, canOverride, loading}: PermissionsProps) {
    const renderedList = restrictions.map(
        restriction =>
            <PermissionItem
                restriction={restriction}
                editable={editable}
                canOverride={canOverride}
                onChange={toggle => onChange(restriction.permission, toggle)}
                key={restriction.permission}
            />
    );

    return (
        <div className={classNames({loading})}>
            <ul className={styles.list} data-qa-ac-permissions>
                {renderedList}
            </ul>
        </div>
    );
}

interface PermissionProps {
    restriction: AccessRestriction;
    editable: boolean;
    canOverride: boolean;
    onChange: (toggle: AccessChangeToggle) => void;
}

function PermissionItem({restriction, editable, canOverride, onChange}: PermissionProps) {
    const {displayName, description, toggle, inherited} = restriction;

    return (
        <li className={styles.item}>
            <div className={styles.controls}>
                <div className={styles.switchContainer}>
                    <Switch checked={toggle === 'Allow'}
                            data-qa-switch={displayName}
                            onChange={(checked) => onChange(checked ? 'Allow' : 'Deny')}
                            disabled={!editable || inherited}
                    />
                    <h5 className={styles.title}>{displayName}</h5>
                </div>
                {editable && canOverride &&
                    <div className={styles.buttonsContainer}>
                        {inherited
                            ?
                            <Button className={classNames(buttons.blankButton, buttons.button)}
                                    icon="sm sm-edit"
                                    label="Override"
                                    onClick={() => onChange(toggle)}/>
                            :
                            <Button icon="sm sm-undo"
                                    label="Restore"
                                    className={classNames(buttons.blankButton, buttons.button)}
                                    onClick={() => onChange('Inherit')}/>
                        }
                    </div>
                }
            </div>
            <p className={styles.description}>{description}</p>
        </li>
    );
}
