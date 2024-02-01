import { getModel, SessionSettingsForm, SessionSettingsFormModel } from "../../sessionSettings/SessionSettingsForm";
import { useProject } from "../projectStore/hooks/useProject";
import { useEffect, useState } from "react";
import { ImageSettingsDto, ProjectSessionApi, SessionTierSpecificationDto } from "../projectSessionApi";
import { useEnvSettingsState } from "./useEnvSettingsState";
import { EnvSessionApi } from "../envSessionApi";
import { useSessionSettingsEditor } from "../../sessionSettings/sessionSettingsEditor";
import classNames from "classnames";
import { useToast } from "@open-smc/application/useToast";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function EnvSessionSettingsPage() {
    const {project} = useProject();
    const {envId, node, permissions: {isOwner}} = useEnvSettingsState();
    const [model, setModel] = useState<SessionSettingsFormModel>();
    const [tiers, setTiers] = useState<SessionTierSpecificationDto[]>();
    const [images, setImages] = useState<ImageSettingsDto[]>();
    const [ready, setReady] = useState(false);
    const [loading, setLoading] = useState(false);
    const {viewModelId} = useEnvSettingsState();
    const sessionSettingsEditor = useSessionSettingsEditor(viewModelId);
    const {showToast} = useToast();

    useEffect(() => {
        (async () => {
            const settings = await EnvSessionApi.getSessionSettings(project.id, envId, node?.id);
            setModel(getModel(settings));
            const tiers = await ProjectSessionApi.getTiers();
            const images = await ProjectSessionApi.getImages();
            setTiers(tiers);
            setImages(images);
            setReady(true);
        })();
    }, [project.id, envId, node?.id]);

    if (!ready) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <div className={classNames({loading})}>
            <SessionSettingsForm
                model={model}
                tiers={tiers}
                images={images}
                canInherit={true}
                editable={isOwner}
                onUpdate={async patch => {
                    try {
                        await sessionSettingsEditor.changeSettings(node ? node.id : envId, patch);
                        setModel({...model, ...patch});
                    } catch (error) {
                        showToast('Error', 'Failed to update settings', 'Error');
                    }
                }}
                onRestoreInheritance={async () => {
                    setLoading(true);
                    try {
                        await sessionSettingsEditor.restoreInheritance(node ? node.id : envId)
                        const settings = await EnvSessionApi.getSessionSettings(project.id, envId, node?.id);
                        setModel(getModel(settings));
                    } finally {
                        setLoading(false);
                    }
                }}
                onOverride={async () => {
                    setLoading(true);
                    try {
                        const settings = await EnvSessionApi.getSessionSettings(project.id, envId, node?.id);
                        const model = getModel(settings);
                        await sessionSettingsEditor.changeSettings(node ? node.id : envId, model);
                        setModel({...model, inherited: false});
                    } finally {
                        setLoading(false);
                    }
                }}
            />
        </div>
    );
}