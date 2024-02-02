import { makeHtml } from "@open-smc/sandbox/src/Html";
import { Sandbox } from "@open-smc/sandbox/src/Sandbox";

const htmlTemplate =
    makeHtml()
        .withData("<p>Hello world!</p>")
        .build()

export function HtmlPage() {
    return (
        <div>
            <div>
                <Sandbox root={htmlTemplate}/>
                <h1>H1 A sudden, unexpected shift of the yield curve</h1>
                <h2>H2 A sudden, unexpected shift of the yield curve</h2>
                <h3>H3 A sudden, unexpected shift of the yield curve</h3>
                <h4>H4 A sudden, unexpected shift of the yield curve</h4>
                <h5>H5 A sudden, unexpected shift of the yield curve</h5>
                <h6>H6 A sudden, unexpected shift of the yield curve</h6>
                <div>
                    <strong>
                        A sudden, unexpected shift of the
                    </strong>
                    <h1>Sample html</h1>
                    <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread
                        (but
                        not most realistic) definition is a parallel shift of the yield curve, either upward or
                        downward, by
                        an even number of basis points such as 100 bp.</p>
                </div>

                <div>
                    <h2>Systemorph Cloud key features and how it works</h2>
                    <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread
                        (but
                        not most realistic) definition is a parallel shift of the yield curve, either upward or
                        downward, by
                        an even number of <strong>basis points</strong> such as 100 bp.</p>

                    <h3>Data Integration and Consolidation</h3>
                    <p>Consolidate data from multiple sources into a unified platform, eliminating data silos and ensuring accurate information.</p>
                </div>


                <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread (but
                    not most realistic) definition is a parallel shift of the yield curve, either upward or downward, by
                    an even number of basis points such as 100 bp.</p>
                <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread (but
                    not most realistic) definition is a parallel shift of the yield curve, either upward or downward, by
                    an even number of basis points such as 100 bp.</p>
                <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread (but
                    not most realistic) definition is a parallel shift of the yield curve, either upward or downward, by
                    an even number of basis points such as 100 bp.</p>
                <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread (but
                    not most realistic) definition is a parallel shift of the yield curve, either upward or downward, by
                    an even number of basis points such as 100 bp.</p>

                <h3>Sample html</h3>
                <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread (but
                    not most realistic) definition is a parallel shift of the yield curve, either upward or downward, by
                    an even number of basis points such as 100 bp.</p>
                <p>A sudden, unexpected shift of the yield curve of predefined form and size. The most widespread (but
                    not most realistic) definition is a parallel shift of the yield curve, either upward or downward, by
                    an even number of basis points such as 100 bp.</p>
            </div>
        </div>
    );
}
