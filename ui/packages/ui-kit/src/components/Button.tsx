import { ButtonHTMLAttributes, forwardRef } from "react";
import Tooltip from "rc-tooltip";
import classNames from "classnames";
import { TooltipProps } from "rc-tooltip/lib/Tooltip";
import styles from "./buttons.module.scss";
import "./tooltip.scss";
import { IconDef, renderIcon } from "./renderIcon";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
    icon?: string | IconDef;
    label?: string;
    labelClassName?: string;
    tooltip?: string;
    tooltipOptions?: Omit<TooltipProps, 'overlay'>;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
    ({
        className,
        type,
        disabled,
        children,
        icon,
        label,
        labelClassName,
        tooltip,
        tooltipOptions,
        onClick,
        ...props
    }, ref) => {
    const createLabel = () => {
        const className = classNames(styles.label, labelClassName);

        if (label) {
            return <span className={className}>{label}</span>;
        }

        return null;
    };

    const buttonLabel = createLabel();
    const buttonIcon = icon && renderIcon(icon);
    const buttonClassname = classNames(styles.button, className);
    const defaultAriaLabel = label ? label : '';

    if (tooltip) {
        const tooltipOverlay = <span className="tooltipOverlay">{tooltip}</span>
        const {mouseEnterDelay, mouseLeaveDelay, placement} = tooltipOptions;

        return (
            <Tooltip
                {...tooltipOptions}
                mouseEnterDelay={mouseEnterDelay}
                mouseLeaveDelay={mouseLeaveDelay}
                placement={placement}
                trigger={["hover"]}
                overlay={tooltipOverlay}
            >
                <button
                    ref={ref}
                    type={type}
                    aria-label={defaultAriaLabel}
                    className={buttonClassname}
                    disabled={disabled}
                    onClick={onClick}
                    {...props}
                >
                    {buttonIcon}
                    {buttonLabel}
                    {children}
                </button>
            </Tooltip>
        );
    }


    return (
        <>
            <button
                type={type}
                aria-label={defaultAriaLabel}
                className={`${className}`}
                disabled={disabled}
                onClick={onClick}
                {...props}
            >
                {buttonIcon}
                {buttonLabel}
                {children}
            </button>
        </>
    );
});
