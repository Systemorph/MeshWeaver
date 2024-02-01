import Slugger from 'github-slugger'
import { kebabCase } from 'lodash';
import { visit } from "unist-util-visit";
import { toString } from 'hast-util-to-string';
import { unified } from 'unified';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import rehypeKatex from 'rehype-katex';
import remarkParse from 'remark-parse';
import remarkRehype from 'remark-rehype';
import rehypeStringify from 'rehype-stringify';
import rehypeRaw from 'rehype-raw';
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize';
import { Element, Root, Text } from 'hast';
import { heading } from 'hast-util-heading';
import { headingRank } from 'hast-util-heading-rank';
import { h } from 'hastscript';
import { createNumbering } from "./numbering";
import { normalize } from 'path-browserify';
import { Path } from '../../shared/utils/path';

export type ParsedMarkdown = ReturnType<ReturnType<typeof getParser>>;

export interface Heading {
    id: string;
    text: string;
    number: string;
    numberNode: Element;
    rank: number;
}

export function getParser(addNumbers: boolean, projectId?: string, environmentId?: string, path?: string, incomingNumber?: string) {
    let headings: Heading[];
    const nextNumber = createNumbering(incomingNumber);
    const slugger = new Slugger();

    function rehypeExtractAndRewriteHeadings() {
        return (tree: Root) => {
            visit(tree, node => heading(node), node => {
                const text = toString(node);
                const id = kebabCase(slugger.slug(text));
                const rank = headingRank(node)!;
                const number = nextNumber(rank);

                const children = (node as Element).children;

                let numberNode: Element;

                if (addNumbers) {
                    numberNode = h('span.heading-number', [number]);
                    children.unshift(numberNode);
                }

                const icon = h('i', {class: "sm sm-link"});

                children.unshift(h('a', {class: "heading-anchor", href: `#${id}`, id: `user-content-${id}`}, [icon]));

                headings.push({
                    id,
                    text,
                    number,
                    numberNode,
                    rank
                });
            });
        }
    }

    const sanitizeSchema = {
        ...defaultSchema,
        attributes: {
            ...defaultSchema.attributes,
            "*": [...defaultSchema.attributes['*'], "style"],
            i: [
                ...(defaultSchema.attributes.i || []),
                ["className", "sm", "sm-link"],
            ],
            a: [
                ...(defaultSchema.attributes.a || []),
                ["className", "heading-anchor"],
            ],
            div: [
                ...(defaultSchema.attributes.div || []),
                ["className", "math", "math-display"],
            ],
            span: [
                ...(defaultSchema.attributes.span || []),
                ['className', 'math', 'math-inline']
            ]
        },
    } as unknown;

    const processor = unified()
        .use(remarkParse)
        .use(remarkMath)
        .use(remarkGfm)
        .use(remarkRehype, {allowDangerousHtml: true})
        .use(rehypeRaw)
        .use(rehypeSanitize, sanitizeSchema)
        .use(rehypeExtractAndRewriteHeadings)
        .use(rehypeKatex)
        .use(rehypeRewriteImagePaths, {projectId, environmentId, activeFilePath: path});

    return function parse(value: string) {
        headings = [];
        const mdastTree = processor.parse(value);
        const tree = processor.runSync(mdastTree);
        return {tree, headings};
    }
}

export function getCompiler(updateNumbers: boolean, incomingNumber?: string) {
    const processor = unified()
        // TODO: use rehype-dom-stringify https://github.com/rehypejs/rehype-dom/tree/main/packages/rehype-dom-stringify (2/18/2022, akravets)
        .use(rehypeStringify);

    const nextNumber = createNumbering(incomingNumber);

    return function compile({tree, headings}: ParsedMarkdown) {
        if (updateNumbers) {
            headings.forEach(heading => {
                const number = nextNumber(heading.rank);
                heading.number = number;
                (heading.numberNode.children[0] as Text).value = number;
            });
        }

        return processor.stringify(tree);
    }
}

export interface rehypeRewriteImagePathsProps {
    projectId: string;
    environmentId: string;
    activeFilePath?: string;
}

export function rehypeRewriteImagePaths({projectId, environmentId, activeFilePath}: rehypeRewriteImagePathsProps) {
    const apiPath = `/api/project/${projectId}/env/${environmentId}/file/download?path=`;
    const srcRegex = /^(?:(\.\.?)?\/)/;

    return (tree: Root) => {
        visit(
            tree,
            (node: any) => node?.tagName === "img" && node?.properties?.src,
            (node: any) => {
                const match = node.properties.src.match(srcRegex);
                if (match) {
                    node.properties.src = match[1]
                        ? apiPath + normalize(Path.dirname(activeFilePath) + '/' + node.properties.src)
                        : apiPath + node.properties.src;
                }
            }
        );
    }
}