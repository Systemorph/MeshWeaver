import { RenderArea } from "./store/RenderArea";
import { useAppSelector } from "./store/hooks";
import '@open-smc/layout/src/contract';
import '@open-smc/data/src/contract';

export default function App() {
    const rootAreaId = useAppSelector(state => state.rootArea);

    return (
        <div className="App">
            <RenderArea id={rootAreaId}/>
        </div>
    );
}