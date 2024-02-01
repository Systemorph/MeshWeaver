import styles from './setupProject.module.scss';
import { Controller, UseFormReturn } from "react-hook-form";
import { ProjectSettings } from "../../../app/projectApi";

export function HomeRegionFieldReadOnly({form: {control}}: { form: UseFormReturn<ProjectSettings> }) {
    return (
        <div className={styles.field} data-qa-field-location>
            <label className={styles.label} htmlFor="homeRegion">Location</label>
            <Controller name="homeRegion"
                        defaultValue={''}
                        control={control}
                        render={({field}) => (<span className={styles.fieldText}>{field.value}</span>)}/>
        </div>
    );
}