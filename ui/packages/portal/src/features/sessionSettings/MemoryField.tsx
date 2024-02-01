import { CpuFieldProps } from "./CpuField";
import styles from "../project/editProject/setupProject.module.scss";
import { Controller } from "react-hook-form";
import Slider from "rc-slider";
import * as React from "react";
import { tooltipHandleRender } from "../../shared/slideHandleRenders";
import { calculateCpu, getMemoryStep, toMarks } from "../../shared/sessionSliderHelpers";
import classNames from "classnames";
import sessionSettings from "./session-settings.module.scss";
import settings from "./session-settings.module.scss";
import { find } from "lodash";

export function MemoryField({form: {control, getValues, setValue}, disabled, onChange, tiers}: CpuFieldProps) {
    return (
        <div className={classNames(styles.field, sessionSettings.memoryField)} data-qa-memory>
            <Controller name="memory"
                        control={control}
                        render={({field}) => {
                            const tier = find(tiers, t => t.systemName === getValues().tier);

                            return (
                                <div className={settings.sliderBox}>
                                    <div className={settings.sliderInfo}>
                                        <label className={styles.label} htmlFor="memory">RAM, GB</label>
                                        <span className={settings.sliderValue}>{field.value}</span>
                                    </div>
                                    <Slider
                                        handleRender={tooltipHandleRender}
                                        onChange={(value) => {
                                            const memory = value as number;
                                            field.onChange(memory);
                                            const cpu = calculateCpu(memory, tier)
                                            setValue('cpu', cpu);
                                            onChange && onChange();
                                        }}
                                        min={tier.minMemory}
                                        max={tier.maxMemory}
                                        step={getMemoryStep(tier)}
                                        value={field.value}
                                        marks={toMarks([
                                            tier.minMemory,
                                            tier.maxMemory,
                                        ])}
                                        disabled={disabled}
                                        data-qa-value={field.value}
                                    />
                                </div>);
                        }}/>
        </div>
    );
}