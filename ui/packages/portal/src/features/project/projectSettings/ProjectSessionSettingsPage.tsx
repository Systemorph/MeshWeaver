import { getModel, SessionSettingsForm, SessionSettingsFormModel } from "../../sessionSettings/SessionSettingsForm";
import { ImageSettingsDto, ProjectSessionApi, SessionTierSpecificationDto } from "../projectSessionApi";
import { useProject } from "../projectStore/hooks/useProject";
import { useEffect, useState } from "react";
import { useProjectPermissions } from "../projectStore/hooks/useProjectPermissions";
import { useSessionSettingsEditor } from "../../sessionSettings/sessionSettingsEditor";
import { useViewModelId } from "../projectStore/hooks/useViewModelId";
import { useToast } from "@open-smc/application/useToast";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function ProjectSessionSettingsPage() {
    const {project} = useProject();
    const [model, setModel] = useState<SessionSettingsFormModel>();
    const [tiers, setTiers] = useState<SessionTierSpecificationDto[]>();
    const [images, setImages] = useState<ImageSettingsDto[]>();
    const [ready, setReady] = useState(false);
    const {isOwner} = useProjectPermissions();
    const viewModelId = useViewModelId();
    const sessionSettingsEditor = useSessionSettingsEditor(viewModelId);
    const {showToast} = useToast();

    useEffect(() => {
        // TODO: add error handling (3/31/2023, akravets)
        (async () => {
            const settings = await ProjectSessionApi.getSessionSettings(project.id);
            setModel(getModel(settings));
            const tiers = await ProjectSessionApi.getTiers();
            const images = await ProjectSessionApi.getImages();
            setTiers(tiers);
            setImages(images);
            setReady(true);
        })();
    }, [project.id]);

    if (!ready) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <SessionSettingsForm
            model={model}
            tiers={tiers}
            images={images}
            editable={isOwner}
            onUpdate={async patch => {
                try {
                    await sessionSettingsEditor.changeSettings(project.id, patch);
                    setModel({...model, ...patch});
                } catch (error) {
                    showToast('Error', 'Failed to update settings', 'Error');
                }
            }}
        />
    );
}