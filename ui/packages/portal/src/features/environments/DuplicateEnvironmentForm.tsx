import styles from "./environments.module.scss";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { Controller, useForm } from "react-hook-form";
import { useCallback, useEffect, useState } from "react";
import { isEmpty } from "lodash";
import classNames from "classnames";
import pDebounce from "p-debounce";
import { EnvApi } from "./envApi";
import { Button } from "@open-smc/ui-kit/components/Button";
import { InputText } from "@open-smc/ui-kit/components/InputText";

type Props = {
    projectId: string;
    environmentId: string;
    onDuplicated?: (env: string) => void;
    onCancel?: () => void;
};

type EnvironmentForm = {
    projectId: string,
    environmentId: string;
    newEnvironmentId?: string
}

export function DuplicateEnvironmentForm({projectId, environmentId, onDuplicated, onCancel}: Props) {
    const {
        handleSubmit,
        formState: {errors, isValidating, isDirty},
        control,
        getValues,
        clearErrors,
        setValue,
        getFieldState,
    } = useForm<EnvironmentForm>({
        mode: 'onChange',
        defaultValues: {
            projectId,
            environmentId
        }
    });

    const [isLoading, setIsLoading] = useState(false);

    useEffect(() => {
        setIsLoading(true);
        (async () => {
            const suggestedName = await EnvApi.suggestId(projectId, environmentId);
            setIsLoading(false);

            if (!getFieldState('newEnvironmentId').isDirty) {
                setValue('newEnvironmentId', suggestedName, {shouldValidate: false});
            }
        })()
    }, []);

    const asyncValidationDebounced = useCallback(pDebounce(async (newEnvironmentId: string) => {
        if (newEnvironmentId === getValues('newEnvironmentId')) {
            setIsLoading(true);
            const messages = await EnvApi.validateId(projectId, newEnvironmentId);

            if (newEnvironmentId === getValues('newEnvironmentId')) {
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

        clearErrors('newEnvironmentId');

        return asyncValidationDebounced(value);
    });

    const submit = async (data: EnvironmentForm, event: any) => {
        event.preventDefault();

        setIsLoading(true);
        await EnvApi.duplicateEnvironment(data.projectId, data.environmentId, data.newEnvironmentId);
        setIsLoading(false);
        onDuplicated && onDuplicated(data.newEnvironmentId);
    };

    const disabled = !isEmpty(errors) || isLoading || isValidating;

    return (
        <div className={classNames(styles.formContainer, !isEmpty(errors) && isDirty ? 'invalid' : '')}
             data-qa-form-environment-duplicate>
            <h2 className={styles.heading} data-qa-title>Duplicate Environment</h2>
            <form onSubmit={handleSubmit(submit)} className={styles.form} autoComplete="off">
                <div className={classNames(styles.labelContainer)}>
                    <label className={styles.label}>Source:</label>
                    <Controller name="environmentId"
                                control={control}
                                render={({field}) => (
                                    <span className={styles.fieldText} data-qa-field-source>{field.value}</span>)
                                }/>
                </div>
                <label className={styles.label}>New environment</label>
                <div className={classNames(styles.idBox)}>
                    <Controller name="newEnvironmentId"
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
                                            invalid={!!fieldState.error}
                                            check
                                            data-qa-field-env-id
                                        />
                                        {errors.newEnvironmentId &&
                                            <small className={styles.error}>{errors.newEnvironmentId.message}</small>}
                                    </div>)
                                }/>
                </div>
                <div className={styles.buttonsContainer}>
                    <Button className={classNames(button.button, button.primaryButton, button.buttonSmall)}
                            type="submit"
                            label="Create"
                            disabled={disabled} data-qa-btn-create/>
                    <Button className={classNames(button.cancelButton, button.button)}
                            type="button"
                            icon="sm sm-close"
                            label="Cancel"
                            onClick={() => onCancel()}
                            data-qa-btn-cancel/>
                </div>
            </form>
        </div>
    );
}