import { Renderer } from "./Renderer";

export class RendererStackTrace extends Array<Renderer> {
    add(renderer: Renderer) {
        return new RendererStackTrace(...this, renderer);
    }
}