import { useAppSelector } from "@open-smc/application/src/app/hooks";
import { RenderArea } from "./app/RenderArea";

export default function App() {
    const rootAreaId = useAppSelector(state => state.rootArea);

    return (
        <div className="App">
            <RenderArea id={rootAreaId}/>
        </div>
    );
}