import styles from "./sidebar.module.scss";
import { PropsWithChildren } from "react";
import {Button, ButtonProps} from "@open-smc/ui-kit/src/components/Button";
import { useNavigate } from "react-router-dom";
import classNames from "classnames";

export const NavigateButton = (props: PropsWithChildren<ButtonProps & { path?: string }>) => {
    let navigate = useNavigate();

    return (<Button
        className={classNames(styles.navigationButton, props.className)}
        onClick={() => navigate(props?.path)}
        {...props}>
        {props?.children}
    </Button>);
}

export const MenuLabel = ({small, text}: { small?: boolean, text: string }) => (
    <label
        className={classNames(styles.label, small && styles.labelSmall)}>
        {text}
    </label>);

type MenuItemProps = { active?: boolean, small?: boolean };

export const MenuItem = ({
                             active,
                             disabled,
                             small,
                             className,
                             children,
                             ...props
                         }: PropsWithChildren<ButtonProps & MenuItemProps>) => (
                             <li className={styles.listItem}>
                                 <Button
                                     {...props}
                                     disabled={disabled}
                                     className={classNames(
                                         styles.button,
                                         small && styles.buttonSmall,
                                         active && styles.active,
                                         disabled && styles.disabled,
                                         className
                                     )}>
                                     {children}
                                 </Button>
                             </li>

);