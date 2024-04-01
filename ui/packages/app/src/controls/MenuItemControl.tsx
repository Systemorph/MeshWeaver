// import { AddHub, useMessageHub } from "../AddHub";
// import { AreaChangedEvent } from "../contract/application.contract";
// import { renderControl } from "../renderControl";
import { useEffect, useState } from 'react';
import styles from "./menuItemControl.module.scss";
// import Dropdown from "rc-dropdown";
import 'rc-dropdown/assets/index.css';
import classNames from "classnames";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { IconDef } from "@open-smc/ui-kit/src/components/renderIcon";
import tinycolor from "tinycolor2";
// import { getParentContextOfType, isControlContextOfType, useControlContext } from "../ControlContext";
// import LayoutStackControl from "./LayoutStackControl";
// import { brandeisBlue, brightGray } from "../colors";
// import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import { ExpandableView } from "../ControlDef";
import { useClickAction } from "../useClickAction";
// import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

export type MenuItemSkin = "LargeButton" | "LargeIcon" | "Link";

export interface MenuItemView extends ExpandableView {
    title?: string;
    icon?: IconDef;
    skin?: MenuItemSkin;
    color?: string;
}

export default function MenuItemControl({
                                            id,
                                            title,
                                            icon,
                                            clickMessage,
                                            expandMessage,
                                            skin,
                                            style,
                                            color,
                                        }: MenuItemView) {
    // const {parentControlContext} = useControlContext<MenuItemView>();
    const [isOpen, setIsOpen] = useState(false);

    const isExpandable = !!expandMessage;

    // const clickAction = useClickAction(clickMessage);

    // const isSideMenu = isControlContextOfType(parentControlContext, LayoutStackControl)
    //     && parentControlContext.boundView.skin === "SideMenu";
    //
    // const isToolbar = getParentContextOfType(
    //     parentControlContext,
    //     LayoutStackControl,
    //     context => context.boundView.skin === "Toolbar"
    // );
    //
    // if (!skin) {
    //     if (isSideMenu) {
    //         skin = "LargeIcon";
    //     }
    // }
    //
    // if (!color) {
    //     if (isToolbar) {
    //         color = brightGray;
    //     }
    //     if (skin === "Link") {
    //         color = brandeisBlue;
    //     }
    // }

    const colorObj = color && tinycolor(color);

    const className = classNames(styles.buttonBox, skin && `skin-${skin}`, {
        [styles.iconOnly]: !title && icon,
        [styles.expandable]: isExpandable,
        light: colorObj?.isLight(),
        dark: colorObj?.isDark()
    });

    const cssVars = {
        ["--main-color"]: colorObj?.toHexString()
    }

    return (
        <div
            className={className}
            style={{...style, ...cssVars}}
        >
            <Button
                icon={icon}
                label={title}
                className={styles.button}
                labelClassName={styles.label}
                // onClick={clickAction}
            >
            </Button>
            {/*{isExpandable &&*/}
            {/*    <Dropdown*/}
            {/*        trigger={['click']}*/}
            {/*        overlay={<ExpandOverlay/>}*/}
            {/*        overlayClassName={styles.overlay}*/}
            {/*        align={{points: ['tr', 'br']}}*/}
            {/*        onVisibleChange={(visible) => setIsOpen(visible)}*/}
            {/*        onOverlayClick={(e) => setIsOpen(false)}*/}
            {/*    >*/}
            {/*        <Button className={styles.chevron}*/}
            {/*                type="button">*/}
            {/*            <i className={classNames(isOpen ? 'sm sm-chevron-up' : 'sm sm-chevron-down')}/>*/}
            {/*        </Button>*/}
            {/*    </Dropdown>*/}
            {/*}*/}
        </div>
    )
}

// function ExpandOverlay() {
//     const {boundView: {expandMessage}} = useControlContext<MenuItemView>();
//     const {address} = expandMessage;
//
//     return (
//         <AddHub address={address}>
//             <ExpandOverlayInner/>
//         </AddHub>
//     )
// }
//
// function ExpandOverlayInner() {
//     const {boundView: {expandMessage}} = useControlContext<MenuItemView>();
//     const {message, area} = expandMessage;
//     const [event, setEvent] = useState<AreaChangedEvent>();
//     const hub = useMessageHub();
//
//     useSubscribeToAreaChanged(hub, area, setEvent);
//
//     useEffect(() => {
//         sendMessage(hub, message);
//     }, [hub, message]);
//
//     if (!event?.view) {
//         return null;
//     }
//
//     return renderControl(event.view);
// }