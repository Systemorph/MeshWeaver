import { defineConfig } from 'vite';
import { viteStaticCopy } from 'vite-plugin-static-copy';

export default defineConfig({
    plugins: [
        viteStaticCopy({
            targets: [
                {
                    src: 'node_modules/highlight.js/styles/*.min.css',
                    dest: './'
                },
                {
                    src: 'node_modules/@primer/css/dist/markdown.css',
                    dest: './'
                },
                {
                    src: 'node_modules/mathjax/dist/mathjax.min.css',
                    dest: './'
                },
                {
                    src: 'node_modules/mermaid/dist/mermaid.min.css',
                    dest: './'
                },
            ],
            structured: true
        })
    ],
    build: {
        emptyOutDir: true,
        minify: false,
        lib: {
            entry: [
                'highlight.ts',
                'htmlUtils.ts'
            ],
            formats: ['es']
        },
        outDir: '../wwwroot'
    },
})