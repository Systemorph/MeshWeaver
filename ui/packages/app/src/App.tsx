import { RenderArea } from "./store/RenderArea";
import { useAppSelector } from "./store/hooks";
import { useEffect } from "react";
import { startSynchronization } from "./store/startSynchronization";
import '@open-smc/layout/src/contract';
import '@open-smc/data/src/contract';

export default function App() {
    const rootAreaId = useAppSelector(state => state.rootArea);

    useEffect(() => startSynchronization(), []);

    return (
        <div className="App">
            <RenderArea id={rootAreaId}/>
        </div>
    );
}