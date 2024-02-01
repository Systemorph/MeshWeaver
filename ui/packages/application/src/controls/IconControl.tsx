import React from "react";
import classNames from "classnames";
import tinycolor from "tinycolor2";
import styles from "./iconControl.module.scss";
import { IconDef, renderIcon } from "@open-smc/ui-kit/components/renderIcon";
import { ControlView } from "../ControlDef";
import { useClickAction } from "../useClickAction";

export type BorderRadius = 'rounded' | 'circle';

export interface IconView extends ControlView {
    icon: IconDef;
    color?: string;
    size?: IconSize;
    background?: boolean;
    borderRadius?: BorderRadius;
}

export type IconSize = "S" | "M" | "L" | "XL" | "XXL";

export default function IconControl({id, icon, color, style, size, background, borderRadius, clickMessage}: IconView) {
    const clickAction = useClickAction(clickMessage);
    const colorObj = color && tinycolor(color);

    const className = classNames(styles.iconBox, "iconBox", size && `size-${size}`, {
        light: colorObj?.isLight(),
        dark: colorObj?.isDark(),
        clickable: clickAction,
        [`${styles.iconBg}`]: background,
        [`icon-${borderRadius}`]: borderRadius,
    });

    const cssVars = {
        ["--main-color"]: colorObj?.toHexString(),
    };

    return (
        <span
            id={id}
            className={className}
            style={{...style, ...cssVars}}
            onClick={clickAction}
        >
            {renderIcon(icon)}
        </span>
    );
}
