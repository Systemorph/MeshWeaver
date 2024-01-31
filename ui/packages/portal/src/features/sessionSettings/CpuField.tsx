import styles from "../project/editProject/setupProject.module.scss";
import { Controller } from "react-hook-form";
import Slider from "rc-slider";
import * as React from "react";
import { SessionTierSpecificationDto } from "../project/projectSessionApi";
import { FormFieldProps } from "./SessionSettingsForm";
import { tooltipHandleRender } from "../../shared/slideHandleRenders";
import { calculateMemory, CPU_STEP, toMarks } from "../../shared/sessionSliderHelpers";
import settings from "./session-settings.module.scss";
import Tooltip from "rc-tooltip";
import classNames from "classnames";
import sessionSettings from "./session-settings.module.scss";
import { TierFormModel } from "./TierField";
import { find } from "lodash";

export interface CpuFieldProps extends FormFieldProps<TierFormModel> {
    tiers: SessionTierSpecificationDto[];
    tooltip?: boolean;
}

export function CpuField({form: {control, getValues, setValue}, tiers, disabled, onChange, tooltip}: CpuFieldProps) {
    return (
        <div className={styles.field} data-qa-cpu>
            <Controller name="cpu"
                        control={control}
                        render={({field}) => {
                            const tier = find(tiers, t => t.systemName === getValues().tier);

                            return (
                                <div className={settings.sliderBox}>
                                    <div className={settings.sliderInfo}>
                                        <label className={styles.label} htmlFor="cpu">CPU</label>
                                        <span className={settings.sliderValue}>{field.value}</span>
                                    </div>

                                    {tooltip &&
                                        <Tooltip overlayClassName={"tooltip"} placement={'right'}
                                                 overlay={<span>CPU-to-memory ratio.<br/> By changing either CPU number or RAM number,<br/> the ratio will be adjusted automatically.</span>}>
                                            <i className={classNames(sessionSettings.fieldInfo, sessionSettings.cpuInfo, "sm sm-info")}/>
                                        </Tooltip>
                                    }

                                    <Slider
                                        handleRender={tooltipHandleRender}
                                        onChange={(value) => {
                                            const cpu = value as number;
                                            field.onChange(cpu);
                                            const memory = calculateMemory(cpu, tier);
                                            setValue('memory', memory);
                                            onChange && onChange();
                                        }}
                                        min={tier.minCpu}
                                        max={tier.maxCpu}
                                        step={CPU_STEP}
                                        value={field.value}
                                        marks={toMarks([
                                            tier.minCpu,
                                            tier.maxCpu,
                                        ])}
                                        disabled={disabled}
                                        data-qa-value={field.value}
                                    />
                                </div>);
                        }}/>
        </div>
    );
}