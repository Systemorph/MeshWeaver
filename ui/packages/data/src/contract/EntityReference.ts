import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.EntityReference")
export class EntityReference<T = unknown> extends WorkspaceReference<T> {
    constructor(public collection: string, public id: string) {
        super(`/${collection}/${id}`);
    }

    get(data: any) {
        return data?.[this.collection]?.[this.id];
    }

    static create<T>(props: EntityReference<T>) {
        return new EntityReference<T>(props.collection, props.id);
    }
}