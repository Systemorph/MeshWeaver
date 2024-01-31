import Tooltip from "rc-tooltip";
import styles from "./badgeControl.module.scss";
import "@open-smc/ui-kit/components/tooltip.scss";
import classNames from "classnames";
import tinycolor from "tinycolor2";
import { ControlView } from "../ControlDef";

export interface BadgeView extends ControlView {
    title?: string;
    subTitle?: string;
    color?: string;
}

export default function BadgeControl({id, title, tooltip, subTitle, color, style}: BadgeView) {
    const colorObj = tinycolor(color);

    const className = classNames(styles.badge, {
        dark: colorObj.isDark(),
        light: colorObj.isLight()
    });

    const cssVars = {
        ["--main-color"]: color,
    };

    const badge = (
        <div id={id} style={{...style, ...cssVars}} className={className}>
            {title && <span className={styles.title}>{title}:</span>}
            {subTitle && <span className={styles.subtitle}>{subTitle}</span>}
        </div>
    );

    if (tooltip) {
        return (
            <Tooltip
                overlayClassName={"tooltip"}
                overlay={<span>{tooltip}</span>}
            >
                {badge}
            </Tooltip>
        );
    }

    return badge;
}