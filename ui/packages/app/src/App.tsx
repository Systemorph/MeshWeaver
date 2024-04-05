import { RenderArea } from "./store/RenderArea";
import { useAppSelector } from "./store/hooks";
import { useEffect } from "react";
import { startSynchronization } from "./store/startSynchronization";
import { sampleApp } from "@open-smc/backend/src/SampleApp";
import '@open-smc/layout/src/contract';
import '@open-smc/data/src/contract';

export default function App() {
    const rootAreaId = useAppSelector(state => state.rootArea);

    useEffect(() => startSynchronization(sampleApp), []);

    return (
        <div className="App">
            <RenderArea id={rootAreaId}/>
        </div>
    );
}