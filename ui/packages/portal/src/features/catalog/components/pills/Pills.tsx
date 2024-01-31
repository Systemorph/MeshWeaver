import styles from "./pills.module.scss";
import {  useSearchParams } from "react-router-dom";
import React from "react";
import classNames from "classnames";

type Pill = {
    label: string;
    key: string;
}

type Props = {
    pills: Pill[];
    currentValue: string;
    onChange?: (value: Pill)=>void
}

export function Pills({pills, currentValue, onChange}: Props) {
    const getClassName = (value: string) =>
        classNames({active: value === currentValue});

    return (
        <ul className={styles.tinyPills}>
            {
                pills?.map((pill) => (
                    <li key={pill.key}>
                        <a
                              onClick={()=>{onChange(pill)}}
                              data-qa-btn={pill.label}
                              data-qa-active={pill.key === currentValue}
                              className={classNames(getClassName(pill.key))}>{pill.label}</a>
                    </li>
                ))
            }
        </ul>
    )
}