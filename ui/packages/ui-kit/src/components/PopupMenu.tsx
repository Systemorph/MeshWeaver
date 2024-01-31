import Menu, { MenuItem, MenuProps, MenuItemProps } from "rc-menu";
import "./rc-menu.scss";

export interface PopupMenuItem extends MenuItemProps {
    label: string;
    icon: string;
    qaAttribute?: string;
}

export interface PopupMenuProps extends MenuProps {
    menuItems?: PopupMenuItem[];
}

export default function PopupMenu({
                                      menuItems,
                                      className,
                                      ...props
                                  }: PopupMenuProps) {
    const renderedItems = menuItems
        .filter((menuItem) => !menuItem.hidden)
        .map((menuItem) => {
            const {label, qaAttribute, icon, ...props} = menuItem;
            return (
                <MenuItem
                    key={label}
                    {...{[qaAttribute]: true}}
                    {...props}
                >
                    <span className={menuItem.icon}></span>
                    <span className="menu-item-label">{menuItem.label}</span>
                </MenuItem>
            )
    });

    return (
        <div className={"popupMenu"}>
            <Menu className={className} {...props}>{renderedItems}</Menu>
        </div>
    );
}
