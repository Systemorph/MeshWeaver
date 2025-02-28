import { defineConfig } from 'vite';
import { viteStaticCopy } from 'vite-plugin-static-copy';
import commonjs from '@rollup/plugin-commonjs';

export default defineConfig({
    plugins: [
        viteStaticCopy({
            targets: [
                {
                    src: 'node_modules/highlight.js/styles/*.min.css',
                    dest: './css/',
                    rename: (name, extension) => `${name}.${extension}`  // Preserve the full filename
                },
                {
                    src: 'node_modules/@primer/css/dist/markdown.css',
                    dest: './css/',
                    rename: (name, extension) => `${name}.${extension}`  // Preserve the full filename
                },
                {
                    src: 'node_modules/mermaid/dist/mermaid.min.js',
                    dest: './'
                },
                {
                    src: 'node_modules/mathjax-full/es5/**/*',
                    dest: './mathjax/'
                }
            ],
            structured: false  // This will prevent maintaining folder structure
        }),
        commonjs({
            include: [/mathjax-full/],
            transformMixedEsModules: true,
            requireReturnsDefault: 'auto'
        })
    ],
    build: {
        emptyOutDir: true,
        minify: false,
        lib: {
            entry: [
                'highlight.ts',
                'mermaid.ts',
                'mathjax.ts'
            ],
            formats: ['es']
        },
        outDir: '../wwwroot',
        rollupOptions: {
            external: [/^mathjax-full\/es5\//]
        }
    },
    resolve: {
        alias: {
            'mathjax-full': 'mathjax-full/es5'
        }
    }
})