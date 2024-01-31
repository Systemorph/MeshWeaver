import { Controller, UseFormReturn } from 'react-hook-form';
import Textarea from 'rc-textarea';
import { ProjectSettings } from "../../../app/projectApi";
import styles from "./setupProject.module.scss";

type Props = {
    form: UseFormReturn<ProjectSettings>;
    disabled?: boolean;
};

export function DescriptionField({form: {control}, disabled = false}: Props) {

    return (
        <div className={styles.field} data-qa-field-description>
            <label className={styles.label} htmlFor="abstract">Description –<i> Optional</i></label>
            <Controller name="abstract"
                        defaultValue=""
                        control={control}
                        render={({field}) => {
                            return <Textarea
                                disabled={disabled}
                                className={styles.description}
                                autoSize={{ minRows: 5, maxRows: 5 }}
                                id={field.name}
                                {...field}/>
                        }}/>
        </div>

    );
}