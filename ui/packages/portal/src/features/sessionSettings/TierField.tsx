import { SessionTierSpecificationDto } from "../project/projectSessionApi";
import styles from "../project/editProject/setupProject.module.scss";
import { Controller } from "react-hook-form";
import { Select } from "@open-smc/ui-kit/components/Select";
import * as React from "react";
import { FormFieldProps } from "./SessionSettingsForm";
import classNames from "classnames";
import sessionSettings from "./session-settings.module.scss";
import Tooltip from "rc-tooltip";
import { find } from "lodash";
import { BaseSelectProps } from "rc-select/lib/BaseSelect";

export interface TierFormModel {
    tier: string;
    cpu: number;
    memory: number;
}

interface TierFieldProps extends FormFieldProps<TierFormModel> {
    tiers: SessionTierSpecificationDto[];
    tooltip?: boolean;
}

export function TierField({
                              form: {control, setValue, getValues},
                              tiers,
                              disabled,
                              onChange,
                              tooltip,
                              getPopupContainer
                          }: TierFieldProps & Pick<BaseSelectProps, 'getPopupContainer'>) {
    const className = classNames(styles.innerContainer, {'single-value': tiers.length === 1});

    return (
        <div className={classNames(styles.field, sessionSettings.field)} data-qa-tier>
            <label className={styles.label} htmlFor="tier">Session tier</label>
            <div className={className}>
                <Controller name="tier"
                            control={control}
                            render={({field}) => {
                                const tier = find(tiers, t => t.systemName === getValues().tier);

                                if (tiers.length === 1) {
                                    return (
                                        <label className={styles.tierName}>{tiers[0].displayName}</label>
                                    );
                                }

                                return (
                                    <Select
                                        value={tier}
                                        options={tiers}
                                        keyBinding={'systemName'}
                                        nameBinding={'displayName'}
                                        disabled={disabled}
                                        onSelect={tier => {
                                            field.onChange(tier.systemName);
                                            const {
                                                minCpu: cpu,
                                                minMemory: memory
                                            } = find(tiers, t => t.systemName === tier.systemName);
                                            setValue('cpu', cpu);
                                            setValue('memory', memory);
                                            onChange && onChange();
                                        }}
                                        getPopupContainer={getPopupContainer}
                                    />
                                );
                            }}/>

                {tooltip &&
                    <Tooltip overlayClassName={"tooltip"} placement={'right'}
                             overlay={<span>Server type used for the session.<br/> By default, Azure D-series servers are used.</span>}>
                        <i className={classNames(sessionSettings.fieldInfo, "sm sm-info")}/>
                    </Tooltip>
                }
            </div>
        </div>
    );
}