import { defineConfig } from 'vite';
import { viteStaticCopy } from 'vite-plugin-static-copy';

export default defineConfig({
    plugins: [
        viteStaticCopy({
            targets: [
                {
                    src: 'node_modules/highlight.js/styles/*.min.css',
                    dest: './'
                }
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