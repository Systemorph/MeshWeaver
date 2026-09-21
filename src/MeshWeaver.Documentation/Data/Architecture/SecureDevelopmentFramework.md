---
Name: Secure Development Framework
Category: Architecture
Description: >-
  How production and development are separated when the platform is delivered into a client's own
  environment: a ringfenced client estate, a narrow interface, and a consultant environment that
  never touches production data. Four rules, what crosses the ringfence, and who approves what.
Icon: <svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3l8 3v6c0 4.5-3.2 8-8 9-4.8-1-8-4.5-8-9V6z'/><path d='M8.5 12l2.5 2.5 4.5-5'/></svg>
---

# Secure Development Framework

This page describes the **target development framework** for building, delivering and operating
software inside a client's own environment: how production and development are separated, what
connects them, and where the security boundary runs. It is written to be shared with any client in
any industry.

The framework rests on four rules:

- **Original data never leaves the client environment.** It lives in the client's production memex,
  inside the client's own cloud subscription, and people reach it only through memex, by their role.
- **No human has access to the production cluster.** Deployment and configuration run through a
  source-control App and its pipelines under the memex system identity; merges, deployments and
  script runs are approved by a client employee on a documented request.
- **Only two things cross the ringfence:** compiled binaries from the public App Store, and people
  logging in with an identity issued by the client.
- **The consultant works on anonymised generic data only**, in a separate interface environment with
  the same structures and no original records.

The reference stack is an enterprise identity provider, a managed Kubernetes service and a hosted
source-control tenant; the same roles map onto any equivalent.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1310 850" width="100%" font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="13" role="img" aria-label="Target development framework: a ringfenced client environment, a narrow interface, and a consultant environment that never touches production data">
<defs>
<marker id="sdf-arr" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="currentColor"/></marker>
<style>
  .sdf text { fill: currentColor; }
  .sdf .muted { opacity: .72; }
  .sdf .panel { fill: currentColor; fill-opacity: .04; }
  .sdf .zone  { fill: currentColor; fill-opacity: .06; }
  .sdf .store { fill: currentColor; fill-opacity: .10; }
  .sdf .edge  { stroke: currentColor; stroke-opacity: .85; fill: none; }
  .sdf .rule  { stroke: currentColor; stroke-opacity: .25; }
  .sdf .b-client { stroke: #4b90d6; }
  .sdf .b-iface  { stroke: #8a8f98; }
  .sdf .b-vendor { stroke: #2aa0b5; }
  .sdf .b-warn   { stroke: #c08a2e; }
  .sdf .t-client { fill: #4b90d6; }
  .sdf .t-vendor { fill: #2aa0b5; }
  .sdf .t-warn   { fill: #c08a2e; }
</style>
</defs>
<g class="sdf">
<text x="655" y="28" text-anchor="middle" font-size="18" font-weight="600">Target development framework: secure production and development</text>
<text x="1290" y="24" text-anchor="end" font-size="11" class="muted">Reference architecture</text>

<rect x="20" y="70" width="390" height="720" rx="10" class="zone b-client" stroke-width="2"/>
<text x="215" y="95" text-anchor="middle" font-size="15" font-weight="600" class="t-client">Client environment</text>
<text x="215" y="112" text-anchor="middle" font-size="12" class="t-client">ringfenced: every access via the client identity provider</text>

<rect x="40" y="130" width="350" height="275" rx="8" class="panel b-client" stroke-width="1.5"/>
<text x="215" y="153" text-anchor="middle" font-weight="600" class="t-client">memex (production) on Kubernetes</text>
<ellipse cx="215" cy="175" rx="60" ry="10" class="store b-client"/>
<rect x="155" y="175" width="120" height="35" class="store b-client"/>
<ellipse cx="215" cy="210" rx="60" ry="10" class="store b-client"/>
<text x="215" y="197" text-anchor="middle" font-weight="600" class="t-client">Original data</text>
<rect x="205" y="226" width="20" height="14" rx="2" fill="currentColor" fill-opacity=".8"/>
<path d="M209 226v-4a6 6 0 0 1 12 0v4" class="edge" stroke-width="2"/>
<text x="215" y="258" text-anchor="middle" font-size="12" class="muted">Access: client users only, via memex, by role</text>
<line x1="55" y1="270" x2="375" y2="270" class="rule"/>
<text x="215" y="288" text-anchor="middle" font-weight="600" class="t-client">Data operations</text>
<text x="215" y="304" text-anchor="middle" font-size="11">anything touching original data runs as an approved</text>
<text x="215" y="318" text-anchor="middle" font-size="11">script executed by memex, logged and reproducible</text>
<line x1="55" y1="330" x2="375" y2="330" class="rule"/>
<text x="215" y="347" text-anchor="middle" font-size="11" font-weight="600" class="t-client">App Store (lives in this memex)</text>
<text x="215" y="361" text-anchor="middle" font-size="11">binaries from the public registry; the project module</text>
<text x="215" y="375" text-anchor="middle" font-size="11">from the project repository; no source code enters</text>
<text x="215" y="394" text-anchor="middle" font-size="11" font-style="italic" class="muted">AI on original data: memex harness with approved models only</text>

<rect x="40" y="418" width="350" height="100" rx="8" class="zone b-client" stroke-width="1.5"/>
<circle cx="66" cy="441" r="8" class="edge b-client" stroke-width="1.6"/>
<path d="M52 464a14 14 0 0 1 28 0" class="edge b-client" stroke-width="1.6"/>
<text x="230" y="439" text-anchor="middle" font-weight="600" class="t-client">Client employee</text>
<text x="230" y="459" text-anchor="middle" font-size="11">approves merge, deploy and script runs on a fully</text>
<text x="230" y="473" text-anchor="middle" font-size="11">documented request (what, why); runs scripts and</text>
<text x="230" y="487" text-anchor="middle" font-size="11">inspects project data; the counterpart for everything</text>
<text x="230" y="501" text-anchor="middle" font-size="11">that requires client hands</text>

<rect x="40" y="532" width="350" height="118" rx="8" class="panel b-warn" stroke-width="1.5"/>
<text x="215" y="554" text-anchor="middle" font-weight="600" class="t-warn">Access and data boundary: security relevant</text>
<text x="215" y="573" text-anchor="middle" font-size="11">No human has access to the cluster: only the source-control</text>
<text x="215" y="587" text-anchor="middle" font-size="11">App, run by the memex system identity.</text>
<text x="215" y="601" text-anchor="middle" font-size="11">People reach data via memex only, by their role.</text>
<text x="215" y="615" text-anchor="middle" font-size="11">Original data never leaves this environment; content goes</text>
<text x="215" y="629" text-anchor="middle" font-size="11">only to security-approved models inside it.</text>
<text x="215" y="643" text-anchor="middle" font-size="11">The vendor only ever gets anonymised generic data.</text>

<rect x="40" y="665" width="350" height="96" rx="8" class="panel b-client" stroke-width="1.5"/>
<text x="215" y="687" text-anchor="middle" font-weight="600" class="t-client">Client identity provider</text>
<text x="215" y="706" text-anchor="middle" font-size="11">secures memex, the cluster and the source-control tenant</text>
<text x="215" y="720" text-anchor="middle" font-size="11">memex system identity: enterprise app in this tenant</text>
<text x="215" y="738" text-anchor="middle" font-size="11" font-style="italic" class="muted">step 2: this set-up is rebuilt here, one instance first;</text>
<text x="215" y="752" text-anchor="middle" font-size="11" font-style="italic" class="muted">the client provides the domain name and the certificate</text>

<rect x="430" y="70" width="440" height="720" rx="10" class="zone b-iface" stroke-width="2"/>
<text x="650" y="95" text-anchor="middle" font-size="15" font-weight="600">Interface</text>
<text x="650" y="112" text-anchor="middle" font-size="12" class="muted">what connects the two environments, and nothing else</text>

<rect x="450" y="130" width="380" height="150" rx="8" class="panel b-vendor" stroke-width="1.5"/>
<text x="640" y="153" text-anchor="middle" font-weight="600" class="t-vendor">The public registry instance</text>
<text x="640" y="169" text-anchor="middle" font-size="12" font-weight="600" class="t-vendor">the source of all binaries</text>
<text x="640" y="191" text-anchor="middle" font-size="11">the only place where the platform, all modules</text>
<text x="640" y="205" text-anchor="middle" font-size="11">and plugins can be seen; compiles the sources and</text>
<text x="640" y="219" text-anchor="middle" font-size="11">distributes the binaries; the App Store of every memex</text>
<text x="640" y="233" text-anchor="middle" font-size="11">downloads its packages from here</text>
<text x="640" y="253" text-anchor="middle" font-size="11" font-style="italic" class="muted">only compiled packages, never source code, no client data:</text>
<text x="640" y="267" text-anchor="middle" font-size="11" font-style="italic" class="muted">not security relevant</text>

<rect x="450" y="305" width="380" height="260" rx="8" class="zone b-client" stroke-width="2"/>
<rect x="796" y="316" width="20" height="14" rx="2" fill="currentColor" fill-opacity=".8"/>
<path d="M800 316v-4a6 6 0 0 1 12 0v4" class="edge" stroke-width="2"/>
<text x="640" y="328" text-anchor="middle" font-weight="600" class="t-client">Client source-control tenant</text>
<text x="640" y="344" text-anchor="middle" font-size="11" font-weight="600" class="t-client">belongs to the client, secured by the client identity provider</text>
<line x1="465" y1="354" x2="815" y2="354" class="rule"/>
<text x="640" y="372" text-anchor="middle" font-size="12" font-weight="600" class="t-client">Project repository</text>
<text x="640" y="387" text-anchor="middle" font-size="11">the project plugin sources of the client memex;</text>
<text x="640" y="401" text-anchor="middle" font-size="11">pull requests and issues of the project;</text>
<text x="640" y="415" text-anchor="middle" font-size="11">merge and deploy approved by a client employee</text>
<line x1="465" y1="425" x2="815" y2="425" class="rule"/>
<text x="640" y="443" text-anchor="middle" font-size="12" font-weight="600" class="t-client">Source-control App and pipelines</text>
<text x="640" y="458" text-anchor="middle" font-size="11">the App is run by the memex system identity;</text>
<text x="640" y="472" text-anchor="middle" font-size="11">the pipelines apply the chart and the cluster configuration</text>
<text x="640" y="486" text-anchor="middle" font-size="11" font-style="italic" class="muted">no human access to the cluster</text>
<line x1="465" y1="496" x2="815" y2="496" class="rule"/>
<text x="640" y="514" text-anchor="middle" font-size="12" font-weight="600" class="t-client">Access</text>
<text x="640" y="529" text-anchor="middle" font-size="11">client identity provider only; vendor staff use dedicated</text>
<text x="640" y="543" text-anchor="middle" font-size="11">client accounts for the project-relevant services</text>
<text x="640" y="557" text-anchor="middle" font-size="11" font-style="italic" class="muted">no system connection to the vendor environment</text>

<rect x="450" y="606" width="380" height="166" rx="8" class="panel b-vendor" stroke-width="1.5"/>
<text x="640" y="628" text-anchor="middle" font-weight="600" class="t-vendor">Interface memex: the vendor's test environment</text>
<text x="640" y="643" text-anchor="middle" font-size="11" class="t-vendor">vendor subscription and tenant; cost billed to the client</text>
<ellipse cx="640" cy="660" rx="85" ry="10" class="store b-vendor"/>
<rect x="555" y="660" width="170" height="26" class="store b-vendor"/>
<ellipse cx="640" cy="686" rx="85" ry="10" class="store b-vendor"/>
<text x="640" y="677" text-anchor="middle" font-weight="600" class="t-vendor">Anonymised generic data</text>
<text x="640" y="707" text-anchor="middle" font-size="11">same structures, no original records; any coding tools;</text>
<text x="640" y="721" text-anchor="middle" font-size="11">reproduce issues, test, load-test and verify fixes</text>
<text x="640" y="738" text-anchor="middle" font-size="11" font-weight="600" class="t-vendor">Access: the vendor · not security relevant</text>
<text x="640" y="752" text-anchor="middle" font-size="11" font-style="italic" class="muted">step 1: the plain set-up, no data at all; syncs project sources</text>
<text x="640" y="765" text-anchor="middle" font-size="11" font-style="italic" class="muted">with the client tenant; installs from the App Store</text>

<rect x="890" y="70" width="400" height="720" rx="10" class="zone b-vendor" stroke-width="2"/>
<text x="1090" y="95" text-anchor="middle" font-size="15" font-weight="600" class="t-vendor">Vendor environment (consultant)</text>
<text x="1090" y="112" text-anchor="middle" font-size="12" class="t-vendor">no client data, ever: not security relevant</text>

<rect x="910" y="130" width="360" height="200" rx="8" class="panel b-vendor" stroke-width="1.5"/>
<text x="1090" y="153" text-anchor="middle" font-weight="600" class="t-vendor">Platform, modules and plugins</text>
<text x="1090" y="169" text-anchor="middle" font-size="11" font-weight="600" class="t-vendor">vendor source-control tenant</text>
<text x="1090" y="191" text-anchor="middle" font-size="11">the platform</text>
<text x="1090" y="205" text-anchor="middle" font-size="11">plugins repository: the project-relevant plugins</text>
<text x="1090" y="219" text-anchor="middle" font-size="11">domain repositories: the industry-specific modules</text>
<text x="1090" y="233" text-anchor="middle" font-size="11" font-style="italic" class="muted">relevant modules agreed per project</text>
<line x1="925" y1="243" x2="1255" y2="243" class="rule"/>
<text x="1090" y="261" text-anchor="middle" font-size="11">visible to the client only through the public registry,</text>
<text x="1090" y="275" text-anchor="middle" font-size="11">as binaries in the App Store</text>
<text x="1090" y="297" text-anchor="middle" font-size="11" font-style="italic" class="muted">the source code stays here</text>
<text x="1090" y="316" text-anchor="middle" font-size="11" font-style="italic" class="muted">licensing per the licence agreement</text>

<rect x="910" y="350" width="360" height="170" rx="8" class="panel b-vendor" stroke-width="1.5"/>
<text x="1090" y="373" text-anchor="middle" font-weight="600" class="t-vendor">Vendor development</text>
<text x="1090" y="391" text-anchor="middle" font-size="11">own instance, own subscriptions (AI coding agents)</text>
<text x="1090" y="405" text-anchor="middle" font-size="11" font-style="italic" class="muted">holds no client data at all</text>
<line x1="925" y1="415" x2="1255" y2="415" class="rule"/>
<text x="1090" y="433" text-anchor="middle" font-size="12" font-weight="600" class="t-vendor">How the vendor works on the project</text>
<text x="1090" y="449" text-anchor="middle" font-size="11">platform and plugin work is committed here;</text>
<text x="1090" y="463" text-anchor="middle" font-size="11">project work happens in the Interface memex (vendor</text>
<text x="1090" y="477" text-anchor="middle" font-size="11">identity) and in the client tenant (dedicated client</text>
<text x="1090" y="491" text-anchor="middle" font-size="11">account), as pull requests</text>
<text x="1090" y="508" text-anchor="middle" font-size="11" font-style="italic" class="muted">nothing is pushed into the client environment from here</text>

<rect x="910" y="540" width="360" height="90" rx="8" class="panel b-warn" stroke-width="1.5"/>
<text x="1090" y="565" text-anchor="middle" font-weight="600" class="t-warn">Principle</text>
<text x="1090" y="586" text-anchor="middle" font-size="12">Vendor employees have no contact</text>
<text x="1090" y="603" text-anchor="middle" font-size="12">with production data.</text>

<rect x="910" y="650" width="360" height="100" rx="8" class="panel b-vendor" stroke-width="1.5"/>
<text x="1090" y="673" text-anchor="middle" font-weight="600" class="t-vendor">Costs</text>
<text x="1090" y="693" text-anchor="middle" font-size="11">the vendor carries only its AI coding-agent subscriptions;</text>
<text x="1090" y="708" text-anchor="middle" font-size="11">the Interface memex subscription and all other project-related</text>
<text x="1090" y="723" text-anchor="middle" font-size="11">services on vendor tenants are passed through to the client;</text>
<text x="1090" y="738" text-anchor="middle" font-size="11">everything in the client environment is the client's cost</text>

<path d="M12 62 H418 V297 H838 V573 H418 V798 H12 Z" class="edge b-client" stroke-width="2" stroke-dasharray="9 6"/>
<text x="834" y="291" text-anchor="end" font-size="11" font-weight="600" class="t-client">Client ringfence</text>

<line x1="910" y1="205" x2="832" y2="205" class="edge" stroke-width="2" marker-end="url(#sdf-arr)"/>
<text x="871" y="197" text-anchor="middle" font-size="11">publish</text>
<line x1="448" y1="230" x2="392" y2="230" class="edge" stroke-width="2" marker-end="url(#sdf-arr)"/>
<text x="420" y="222" text-anchor="middle" font-size="11">App Store</text>
<text x="420" y="245" text-anchor="middle" font-size="11">binaries</text>
<polyline points="830,262 852,262 852,700 832,700" class="edge" stroke-width="2" marker-end="url(#sdf-arr)"/>
<line x1="448" y1="340" x2="392" y2="340" class="edge" stroke-width="2" marker-end="url(#sdf-arr)"/>
<text x="420" y="332" text-anchor="middle" font-size="11">deploy</text>
<line x1="390" y1="365" x2="448" y2="365" class="edge" stroke-width="2" marker-end="url(#sdf-arr)"/>
<text x="420" y="378" text-anchor="middle" font-size="11">issues</text>
<line x1="640" y1="569" x2="640" y2="603" class="edge" stroke-width="2" marker-start="url(#sdf-arr)" marker-end="url(#sdf-arr)"/>
<text x="658" y="591" font-size="11">Project sources: pull and push</text>
<line x1="1090" y1="350" x2="1090" y2="332" class="edge" stroke-width="2" marker-end="url(#sdf-arr)"/>
<text x="1100" y="345" font-size="11">commits</text>
<polyline points="910,480 880,480 880,680 832,680" class="edge" stroke-width="2" stroke-dasharray="5 4" marker-end="url(#sdf-arr)"/>
<text x="867" y="592" text-anchor="middle" font-size="10" transform="rotate(-90 867 592)">vendor staff</text>

<text x="655" y="820" text-anchor="middle" font-size="12" class="muted">No system connection between the vendor environment and the client environment. Security relevance stops at the client ringfence.</text>
<text x="655" y="838" text-anchor="middle" font-size="12" class="muted">The only crossings: compiled binaries from the public registry, and people logging in with a client identity.</text>
</g>
</svg>
