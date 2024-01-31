import styles from "../project/editProject/setupProject.module.scss";
import { Controller } from "react-hook-form";
import { InputNumber } from "@open-smc/ui-kit/components/InputNumber";
import { FormFieldProps, SessionSettingsFormModel } from "./SessionSettingsForm";
import sessionSettings from "./session-settings.module.scss";
import classNames from "classnames";
import Tooltip from "rc-tooltip";
import { ReactNode } from "react";

type IdleTimeoutProps = FormFieldProps<SessionSettingsFormModel> & {
    name: 'sessionIdleTimeout' | 'applicationIdleTimeout';
    description: string;
    tooltip?: ReactNode;
}

export function IdleTimeout({
                                form: {control},
                                name,
                                description,
                                tooltip,
                                disabled,
                                onChange,
                                onBlur,
                                onKeydown
                            }: IdleTimeoutProps) {
    return (
        <div className={classNames(styles.field, sessionSettings.field, sessionSettings.timeoutField)}
             data-qa-idle-timeout>
            <label className={styles.label} htmlFor="idleTimeout">{description}</label>

            {tooltip &&
                <Tooltip overlayClassName={"tooltip"} placement={'right'}
                         overlay={tooltip}>
                    <i className={classNames(sessionSettings.fieldInfo, sessionSettings.timeoutInfo, "sm sm-info")}/>
                </Tooltip>
            }

            <Controller name={name}
                        control={control}
                        rules={{
                            validate: value => value > 0
                        }}
                        render={({field, fieldState}) => (
                            <div>
                                <InputNumber
                                    value={field.value}
                                    onChange={(value) => {
                                        field.onChange(value);
                                        onChange && onChange();
                                    }}
                                    onBlur={onBlur}
                                    onKeyDown={onKeydown}
                                    disabled={disabled}
                                    invalid={!!fieldState.error}
                                />
                                <span className={sessionSettings.minutes}>minutes</span>
                            </div>

                        )}/>
        </div>
    );
}