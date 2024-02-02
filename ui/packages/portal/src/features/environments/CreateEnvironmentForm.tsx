import styles from "./environments.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { Controller, useForm } from "react-hook-form";
import React, { useCallback, useEffect, useState } from "react";
import { isEmpty } from "lodash";
import classNames from "classnames";
import pDebounce from "p-debounce";
import { EnvApi } from "./envApi";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

type EnvironmentForm = {
    projectId: string,
    environmentId: string;
}

type Props = {
    projectId: string;
    onCreated?: (env: string) => void;
    onCancel?: () => void;
};

export function CreateEnvironmentForm({projectId, onCreated, onCancel}: Props) {
    const {
            handleSubmit,
            formState: {errors, isValidating, isDirty},
            control,
            clearErrors,
            getValues,
            trigger
        } = useForm<EnvironmentForm>({
                mode: 'onChange',
                defaultValues: {
                    environmentId: '',
                    projectId
                }
            }
        )
    ;
    const [isLoading, setIsLoading] = useState(false);

    const asyncValidationDebounced = useCallback(pDebounce(async (environmentId: string) => {
        if (environmentId === getValues('environmentId')) {
            setIsLoading(true);
            const messages = await EnvApi.validateId(projectId, environmentId);

            if (environmentId === getValues('environmentId')) {
                setIsLoading(false)
                return isEmpty(messages) || messages.join(' ');
            }
        }

        return false;
    }, 300), []);


    const validate = (async (value: string) => {
        if (isEmpty(value)) {
            if (isLoading) {
                setIsLoading(false);
            }
            return 'Name is required';
        }

        clearErrors('environmentId');

        return asyncValidationDebounced(value);
    });

    const submit = async (data: EnvironmentForm, event: any) => {
        event.preventDefault();
        setIsLoading(true);
        await EnvApi.createEnvironment(data.projectId, data.environmentId);
        setIsLoading(false);
        onCreated && onCreated(data.environmentId);
    };

    const disabled = !isEmpty(errors) || isLoading || isValidating;

    useEffect(() => {
        trigger();
    }, []);

    return (
        <React.Suspense fallback={<div className={loader.loading}>Loading...</div>}>
            <div className={styles.envContainer}>
                <div className={classNames(styles.formContainer, !isEmpty(errors) && isDirty ? 'invalid' : '')}
                     data-qa-form-environment-new>
                    <h2 className={styles.heading} data-qa-title>New Environment</h2>
                    <form onSubmit={handleSubmit(submit)} className={styles.form} autoComplete="off">
                        <div className={classNames(styles.idBox)}>
                            <Controller name="environmentId"
                                        defaultValue=""
                                        control={control}
                                        rules={{
                                            validate
                                        }}
                                        render={({field, fieldState}) => (
                                            <div className={styles.idField}>
                                                <InputText
                                                    autoFocus={true}
                                                    id={field.name}
                                                    {...field}
                                                    processing={isLoading}
                                                    invalid={!!fieldState.error && fieldState.isDirty}
                                                    data-qa-field-envid
                                                />

                                                {fieldState.isDirty && errors.environmentId &&
                                                    <small
                                                        className={styles.error}>{errors.environmentId.message}</small>}
                                            </div>)
                                        }/>
                        </div>

                        <div className={styles.buttonsContainer}>
                            <Button className={classNames(button.button, button.primaryButton, button.buttonSmall)}
                                    type="submit"
                                    label="create"
                                    disabled={disabled}
                                    data-qa-btn-create/>
                            <Button className={classNames(button.cancelButton, button.button)}
                                    label="Cancel"
                                    type="button"
                                    icon="sm sm-close"
                                    onClick={() => onCancel()} data-qa-btn-cancel>
                            </Button>
                        </div>
                    </form>
                </div>
            </div>
        </React.Suspense>
    );
}