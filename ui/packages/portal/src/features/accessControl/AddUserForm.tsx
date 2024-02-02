import { Button } from "@open-smc/ui-kit/src/components/Button";
import { Controller, useForm, UseFormReturn } from "react-hook-form";
import { isEmpty } from "lodash";
import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import 'rc-checkbox/assets/index.css';
import { useEffect } from "react";
import { AccessGroup, GroupChangeToggle } from "./accessControl.contract";
import styles from "./addForm.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import classNames from "classnames";

interface AddUserFormFields {
    user: string;
    toggle: GroupChangeToggle;
}

interface Props {
    addToGroup: AccessGroup;
    onSubmit: (formFields: AddUserFormFields) => void;
    onCancel: () => void;
}

export function AddUserForm({addToGroup, onSubmit, onCancel}: Props) {
    const form = useForm<AddUserFormFields>({
        mode: 'onChange',
        defaultValues: {
            user: '',
            toggle: 'Add'
        }
    });

    const {handleSubmit, formState: {isSubmitted, errors, dirtyFields, isValidating}, trigger} = form;

    const disabled = isSubmitted || !isEmpty(errors) || isEmpty(dirtyFields) || isValidating;

    useEffect(() => {
        trigger();
    }, []);

    return (
        <form data-qa-dialog-add-user onSubmit={handleSubmit(onSubmit)} className={styles.form} autoComplete="off">
            <h3 className={styles.title}>Add user to group: <span data-qa-group-name>{addToGroup.displayName}</span>
            </h3>

            <UserField form={form}/>
            {/*<GroupChangeToggleField form={form}/>*/}

            <div className={styles.buttons}>
                <Button className={classNames(button.primaryButton, button.button)}
                        type="submit"
                        icon="sm sm-plus"
                        label="Add"
                        disabled={disabled}
                        data-qa-btn-add
                />
                <Button className={classNames(button.cancelButton, button.button)}
                        type="button"
                        icon="sm sm-close"
                        label="Cancel"
                        onClick={onCancel}
                        data-qa-btn-cancel
                />
            </div>
        </form>
    );
}

type FieldProps = {form: UseFormReturn<AddUserFormFields>};

const emailRegexp = /^(([^<>()[\]\\.,;:\s@"]+(\.[^<>()[\]\\.,;:\s@"]+)*)|(".+"))@((\[[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\])|(([a-zA-Z\-0-9]+\.)+[a-zA-Z]{2,}))$/;

function UserField({form}: FieldProps) {
    const {control} = form;

    return (
        <div data-qa-field-user className={styles.fieldGroup}>
            <label htmlFor="user" className={styles.label}>Email</label>
            <Controller name="user"
                        control={control}
                        rules={{
                            required: 'User is required',
                            pattern: emailRegexp
                        }}
                        render={({field, fieldState}) => {
                            return (
                                <>
                                    <InputText
                                        data-qa-email
                                        id={field.name}
                                        autoFocus
                                        {...field}
                                        invalid={fieldState.isDirty && !!fieldState.error}
                                    />
                                    {fieldState.isDirty && fieldState.error &&
                                        <small className={styles.error}>{fieldState.error.message}</small>}
                                </>
                            )
                        }}/>
        </div>
    );
}