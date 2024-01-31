import { Controller, UseFormReturn } from "react-hook-form";
import Checkbox from "rc-checkbox";
import { GroupChangeToggle } from "./accessControl.contract";

interface FormFields {
    toggle: GroupChangeToggle;
}

type FieldProps<T extends FormFields> = { form: UseFormReturn<T> };

export function GroupChangeToggleField<T extends FormFields>({form}: FieldProps<T>) {
    const {control} = form as any;

    return (
        <div data-qa-field-toggle>
            <label>Permission</label>
            <Controller name="toggle"
                        control={control}
                        render={({field: {name, onChange, value}, fieldState}) => {
                            return (
                                <>
                                    <div>
                                        <label htmlFor="add">
                                            <Checkbox
                                                id="add"
                                                name={name}
                                                onClick={e => value !== 'Add' && onChange('Add')}
                                                checked={value === 'Add'}
                                                type="radio"
                                            />
                                            <span>Allow</span>
                                        </label>
                                    </div>
                                    <div>
                                        <label htmlFor="remove">
                                            <Checkbox
                                                id="remove"
                                                name={name}
                                                onClick={e => value !== 'Remove' && onChange('Remove')}
                                                checked={value === 'Remove'}
                                                type="radio"
                                            />
                                            <span>Deny</span>
                                        </label>
                                    </div>
                                </>
                            )
                        }}/>
        </div>
    );
}