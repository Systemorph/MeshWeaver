import {NavLink, Outlet} from "react-router-dom";
import pills from '../../shared/components/pills.module.scss';

export type AccessControlParams = {
    readonly groupId: string;
    readonly userId: string;
}

export function AccessControlPage() {
    const className: any = ({isActive}: any) => isActive ? 'active' : undefined;

    return (
        <div>
            <div className={pills.wrapper}>
                <ul className={pills.list}>
                    <li>
                        <NavLink data-qa-tab-permissions to="permissions" className={pills.item}>
                            <i className="sm sm-lock"/>
                            Permissions
                        </NavLink>
                    </li>
                    <li>
                        <NavLink data-qa-tab-groups to="groups" className={pills.item}>
                            <i className="sm sm-team"/>
                            Groups
                        </NavLink>
                    </li>
                    <li>
                        <NavLink data-qa-tab-users to="users" className={pills.item}>
                            <i className="sm sm-user"/>
                            Users
                        </NavLink>
                    </li>
                </ul>
            </div>
            <Outlet/>
        </div>
    );
}