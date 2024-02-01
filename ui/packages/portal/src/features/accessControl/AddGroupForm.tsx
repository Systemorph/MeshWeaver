import { Button } from "@open-smc/ui-kit/components/Button";
import { Controller, useForm, UseFormReturn } from "react-hook-form";
import styles from "./addForm.module.scss";
import { isEmpty } from "lodash";
import 'rc-checkbox/assets/index.css';
import { useEffect } from "react";
import { AccessGroup, AccessUser, GroupChangeToggle } from "./accessControl.contract";
import { Select } from "@open-smc/ui-kit/components/Select";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import classNames from "classnames";

interface AddGroupFormFields {
    group: AccessGroup;
    toggle: GroupChangeToggle;
}

interface Props {
    user: AccessUser;
    groups: AccessGroup[];
    onSubmit: (formFields: AddGroupFormFields) => void;
    onCancel: () => void;
}

export function AddGroupForm({user, groups, onSubmit, onCancel}: Props) {
    const form = useForm<AddGroupFormFields>({
        mode: 'onChange',
        defaultValues: {
            group: null,
            toggle: 'Add'
        }
    });

    const {handleSubmit, formState: {isSubmitted, errors, dirtyFields, isValidating}, trigger} = form;

    const disabled = isSubmitted || !isEmpty(errors) || isEmpty(dirtyFields) || isValidating;

    useEffect(() => {
        trigger();
    }, []);

    return (
        <form data-qa-dialog-add-user-to-group onSubmit={handleSubmit(onSubmit)} className={styles.form}
              autoComplete="off">
            <h3 className={styles.title}>Add user to group</h3>
            <div className={styles.user}>
                User:
                <span className={styles.userName} data-qa-email> {user.name}</span>
            </div>
            <GroupField form={form} groups={groups}/>
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

type GroupFieldProps = {form: UseFormReturn<AddGroupFormFields>, groups: AccessGroup[]};

const getPopupContainer = () => document.getElementById('select-container');

function GroupField({form, groups}: GroupFieldProps) {
    const {control} = form;

    return (
        <div className={styles.fieldGroup} data-qa-field-group>
            <label htmlFor="group" className={styles.label}>Select group</label>
            <Controller name="group"
                        control={control}
                        rules={{
                            required: 'Group is required'
                        }}
                        render={({field, fieldState}) => {
                            return (
                                <>
                                    <Select
                                        data-qa-group-name
                                        className={styles.select}
                                        id={field.name}
                                        value={field.value}
                                        options={groups}
                                        keyBinding={'name'}
                                        nameBinding={'displayName'}
                                        onSelect={field.onChange}
                                        getPopupContainer={getPopupContainer}
                                    />
                                    {fieldState.isDirty && fieldState.error &&
                                        <small className={styles.error}>{fieldState.error.message}</small>}
                                </>
                            )
                        }}/>
        </div>
    );
}