---
Name: Data Mesh
Category: Documentation
Description: Turn scattered data into addressable, combinable data products with clear ownership and governance
Icon: /static/DocContent/DataMesh/icon.svg
---

# The Data Problem

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 860 200" style="width:100%;max-width:860px;display:block;margin:24px auto;">
  <rect x="10" y="10" width="155" height="34" rx="10" fill="#1e88e5"/>
  <text x="87" y="32" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Underwriting</text>
  <rect x="14" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="48" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Oracle DB</text>
  <rect x="93" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="127" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Excel</text>
  <rect x="14" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="48" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">SharePoint</text>
  <rect x="93" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="127" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Legacy App</text>
  <line x1="173" y1="15" x2="173" y2="115" stroke="currentColor" stroke-opacity=".15" stroke-width="1" stroke-dasharray="5 3"/>
  <rect x="180" y="10" width="155" height="34" rx="10" fill="#e53935"/>
  <text x="257" y="32" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Risk</text>
  <rect x="184" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="218" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">SQL Server</text>
  <rect x="263" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="297" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Python</text>
  <rect x="184" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="218" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">R Studio</text>
  <rect x="263" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="297" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Excel</text>
  <line x1="343" y1="15" x2="343" y2="115" stroke="currentColor" stroke-opacity=".15" stroke-width="1" stroke-dasharray="5 3"/>
  <rect x="350" y="10" width="155" height="34" rx="10" fill="#f57c00"/>
  <text x="427" y="32" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Claims</text>
  <rect x="354" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="388" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Claims DB</text>
  <rect x="433" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="467" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Excel</text>
  <rect x="354" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="388" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Doc Store</text>
  <rect x="433" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="467" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Imaging</text>
  <line x1="513" y1="15" x2="513" y2="115" stroke="currentColor" stroke-opacity=".15" stroke-width="1" stroke-dasharray="5 3"/>
  <rect x="520" y="10" width="155" height="34" rx="10" fill="#43a047"/>
  <text x="597" y="32" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Finance</text>
  <rect x="524" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="558" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">SAP</text>
  <rect x="603" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="637" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Power BI</text>
  <rect x="524" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="558" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Postgres</text>
  <rect x="603" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="637" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Excel</text>
  <line x1="683" y1="15" x2="683" y2="115" stroke="currentColor" stroke-opacity=".15" stroke-width="1" stroke-dasharray="5 3"/>
  <rect x="690" y="10" width="155" height="34" rx="10" fill="#8e24aa"/>
  <text x="767" y="32" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Reserving</text>
  <rect x="694" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="728" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Access DB</text>
  <rect x="773" y="52" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="807" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">MongoDB</text>
  <rect x="694" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="728" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">REST APIs</text>
  <rect x="773" y="84" width="68" height="28" rx="6" fill="#78909c"/>
  <text x="807" y="102" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Excel</text>
  <text x="430" y="148" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-family="sans-serif" font-size="15" font-weight="600">Each domain maintains its own version of the truth.</text>
  <text x="430" y="170" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-family="sans-serif" font-size="14">Reconciliation is a full-time job.</text>
</svg>

---

# AI Amplifies the Chaos

One person with AI agents can now produce what used to take a team. **More output = more data. More data without structure = more chaos.**

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 700 220" style="width:100%;max-width:700px;display:block;margin:24px auto;">
  <defs><marker id="ah" markerWidth="7" markerHeight="5" refX="7" refY="2.5" orient="auto"><path d="M0,0 L7,2.5 L0,5" fill="currentColor" fill-opacity=".3"/></marker></defs>
  <rect x="20" y="75" width="100" height="60" rx="30" fill="#5c6bc0"/>
  <text x="70" y="110" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="15" font-weight="600">1 Person</text>
  <line x1="120" y1="90" x2="195" y2="38" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <line x1="120" y1="98" x2="195" y2="88" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <line x1="120" y1="112" x2="195" y2="138" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <line x1="120" y1="120" x2="195" y2="188" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <rect x="195" y="14" width="130" height="44" rx="10" fill="#7e57c2"/>
  <text x="260" y="41" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Research Agent</text>
  <rect x="195" y="68" width="130" height="44" rx="10" fill="#7e57c2"/>
  <text x="260" y="95" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Analysis Agent</text>
  <rect x="195" y="122" width="130" height="44" rx="10" fill="#7e57c2"/>
  <text x="260" y="149" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Reporting Agent</text>
  <rect x="195" y="176" width="130" height="44" rx="10" fill="#7e57c2"/>
  <text x="260" y="203" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Execution Agent</text>
  <line x1="325" y1="36" x2="395" y2="36" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <line x1="325" y1="90" x2="395" y2="90" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <line x1="325" y1="144" x2="395" y2="144" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <line x1="325" y1="198" x2="395" y2="198" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah)"/>
  <rect x="395" y="14" width="120" height="44" rx="10" fill="#26a69a"/>
  <text x="455" y="41" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Market scans</text>
  <rect x="395" y="68" width="120" height="44" rx="10" fill="#26a69a"/>
  <text x="455" y="95" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Risk models</text>
  <rect x="395" y="122" width="120" height="44" rx="10" fill="#26a69a"/>
  <text x="455" y="149" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Board decks</text>
  <rect x="395" y="176" width="120" height="44" rx="10" fill="#26a69a"/>
  <text x="455" y="203" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">System updates</text>
  <text x="590" y="100" text-anchor="middle" font-size="40" opacity=".3">&#x1F4A5;</text>
  <text x="590" y="134" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="13" font-weight="600">exponential</text>
  <text x="590" y="150" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="13" font-weight="600">output</text>
</svg>

---

# The Missing Piece: Combinability

> Take this contract here, combine it with that portfolio there, overlay this exposure data, and show me the result.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 700 230" style="width:100%;max-width:700px;display:block;margin:24px auto;">
  <defs><marker id="ah2" markerWidth="7" markerHeight="5" refX="7" refY="2.5" orient="auto"><path d="M0,0 L7,2.5 L0,5" fill="currentColor" fill-opacity=".3"/></marker></defs>
  <rect x="10" y="5" width="140" height="36" rx="10" fill="#1e88e5"/>
  <text x="80" y="28" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Underwriting</text>
  <rect x="10" y="47" width="140" height="36" rx="10" fill="#e53935"/>
  <text x="80" y="70" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Risk</text>
  <rect x="10" y="89" width="140" height="36" rx="10" fill="#f57c00"/>
  <text x="80" y="112" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Claims</text>
  <rect x="10" y="131" width="140" height="36" rx="10" fill="#43a047"/>
  <text x="80" y="154" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Finance</text>
  <rect x="10" y="173" width="140" height="36" rx="10" fill="#8e24aa"/>
  <text x="80" y="196" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Reserving</text>
  <line x1="150" y1="23" x2="260" y2="95" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <line x1="150" y1="65" x2="260" y2="100" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <line x1="150" y1="107" x2="260" y2="105" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <line x1="150" y1="149" x2="260" y2="110" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <line x1="150" y1="191" x2="260" y2="115" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <rect x="260" y="80" width="170" height="56" rx="14" fill="#1e88e5"/>
  <text x="345" y="106" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="16" font-weight="700">Data Mesh</text>
  <text x="345" y="124" text-anchor="middle" fill="#fff" opacity=".7" font-family="sans-serif" font-size="12">addressable &amp; governed</text>
  <line x1="430" y1="97" x2="510" y2="44" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <line x1="430" y1="108" x2="510" y2="104" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <line x1="430" y1="119" x2="510" y2="164" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" marker-end="url(#ah2)"/>
  <rect x="510" y="25" width="140" height="38" rx="10" fill="#43a047"/>
  <text x="580" y="49" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Knowledge</text>
  <rect x="510" y="85" width="140" height="38" rx="10" fill="#43a047"/>
  <text x="580" y="109" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Capital</text>
  <rect x="510" y="145" width="140" height="38" rx="10" fill="#43a047"/>
  <text x="580" y="169" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="600">Profit</text>
</svg>

---

# Data Products: The Building Block

Each data product is a self-contained unit with clear boundaries.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 700 320" style="width:100%;max-width:700px;display:block;margin:24px auto;">
  <rect x="260" y="120" width="180" height="70" rx="14" fill="#43a047"/>
  <text x="350" y="154" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="17" font-weight="700">Data Product</text>
  <text x="350" y="174" text-anchor="middle" fill="#fff" opacity=".7" font-family="sans-serif" font-size="13">@org/product-name</text>
  <line x1="260" y1="140" x2="175" y2="55" stroke="#43a047" opacity=".5" stroke-width="1.5"/>
  <line x1="440" y1="140" x2="525" y2="55" stroke="#43a047" opacity=".5" stroke-width="1.5"/>
  <line x1="260" y1="155" x2="145" y2="165" stroke="#43a047" opacity=".5" stroke-width="1.5"/>
  <line x1="440" y1="155" x2="555" y2="165" stroke="#43a047" opacity=".5" stroke-width="1.5"/>
  <line x1="290" y1="190" x2="175" y2="275" stroke="#43a047" opacity=".5" stroke-width="1.5"/>
  <line x1="410" y1="190" x2="525" y2="275" stroke="#43a047" opacity=".5" stroke-width="1.5"/>
  <rect x="60" y="20" width="130" height="62" rx="10" fill="currentColor" fill-opacity=".08" stroke="#43a047" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="125" y="46" text-anchor="middle" fill="currentColor" font-family="sans-serif" font-size="14" font-weight="700">Owner</text>
  <text x="125" y="64" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">accountable person</text>
  <rect x="460" y="20" width="130" height="62" rx="10" fill="currentColor" fill-opacity=".08" stroke="#43a047" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="525" y="46" text-anchor="middle" fill="currentColor" font-family="sans-serif" font-size="14" font-weight="700">Schema</text>
  <text x="525" y="64" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">typed contracts</text>
  <rect x="10" y="130" width="145" height="62" rx="10" fill="currentColor" fill-opacity=".08" stroke="#43a047" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="82" y="156" text-anchor="middle" fill="currentColor" font-family="sans-serif" font-size="14" font-weight="700">Service Levels</text>
  <text x="82" y="174" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">quality &amp; freshness</text>
  <rect x="545" y="130" width="145" height="62" rx="10" fill="currentColor" fill-opacity=".08" stroke="#43a047" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="618" y="156" text-anchor="middle" fill="currentColor" font-family="sans-serif" font-size="14" font-weight="700">Access Control</text>
  <text x="618" y="174" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">read &amp; write perms</text>
  <rect x="60" y="250" width="130" height="62" rx="10" fill="currentColor" fill-opacity=".08" stroke="#43a047" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="125" y="276" text-anchor="middle" fill="currentColor" font-family="sans-serif" font-size="14" font-weight="700">Change Cycles</text>
  <text x="125" y="294" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">versioned evolution</text>
  <rect x="460" y="250" width="130" height="62" rx="10" fill="currentColor" fill-opacity=".08" stroke="#43a047" stroke-opacity=".5" stroke-width="1.2"/>
  <text x="525" y="276" text-anchor="middle" fill="currentColor" font-family="sans-serif" font-size="14" font-weight="700">Address</text>
  <text x="525" y="294" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">unique mesh path</text>
</svg>

---

# From Silos to a Mesh

Data products are **nodes in a graph** — each with a clear owner, schema, and service-level commitment. Arrows show data flowing from producers to consumers, with the agreed SLO on each connection.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 860 500" style="width:100%;max-width:860px;display:block;margin:24px auto;">
  <defs><marker id="ma" markerWidth="7" markerHeight="5" refX="7" refY="2.5" orient="auto"><path d="M0,0 L7,2.5 L0,5" fill="currentColor" fill-opacity=".35"/></marker></defs>
  <rect x="5" y="5" width="395" height="200" rx="12" fill="#1e88e5" fill-opacity=".1"/>
  <rect x="460" y="5" width="395" height="200" rx="12" fill="#e53935" fill-opacity=".1"/>
  <rect x="5" y="290" width="395" height="200" rx="12" fill="#43a047" fill-opacity=".1"/>
  <rect x="460" y="290" width="395" height="200" rx="12" fill="#8e24aa" fill-opacity=".1"/>
  <text x="200" y="30" text-anchor="middle" fill="#42a5f5" font-family="sans-serif" font-size="16" font-weight="700">Underwriting</text>
  <text x="657" y="30" text-anchor="middle" fill="#ef5350" font-family="sans-serif" font-size="16" font-weight="700">Risk</text>
  <text x="200" y="313" text-anchor="middle" fill="#66bb6a" font-family="sans-serif" font-size="16" font-weight="700">Finance</text>
  <text x="657" y="313" text-anchor="middle" fill="#ce93d8" font-family="sans-serif" font-size="16" font-weight="700">Reserving</text>
  <rect x="25" y="50" width="130" height="32" rx="8" fill="#1e88e5"/>
  <text x="90" y="71" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Submission</text>
  <rect x="195" y="50" width="130" height="32" rx="8" fill="#1e88e5"/>
  <text x="260" y="71" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Offering</text>
  <rect x="25" y="105" width="130" height="32" rx="8" fill="#1e88e5"/>
  <text x="90" y="126" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Policy</text>
  <rect x="195" y="105" width="130" height="32" rx="8" fill="#1e88e5"/>
  <text x="260" y="126" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Binding</text>
  <rect x="480" y="50" width="130" height="32" rx="8" fill="#e53935"/>
  <text x="545" y="71" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Exposure</text>
  <rect x="650" y="50" width="130" height="32" rx="8" fill="#e53935"/>
  <text x="715" y="71" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Cat Model</text>
  <rect x="480" y="105" width="130" height="32" rx="8" fill="#e53935"/>
  <text x="545" y="126" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Accumulation</text>
  <rect x="650" y="105" width="130" height="32" rx="8" fill="#e53935"/>
  <text x="715" y="126" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Loss Scenario</text>
  <rect x="25" y="335" width="130" height="32" rx="8" fill="#43a047"/>
  <text x="90" y="356" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Premium</text>
  <rect x="195" y="335" width="130" height="32" rx="8" fill="#43a047"/>
  <text x="260" y="356" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Claims Paid</text>
  <rect x="25" y="390" width="130" height="32" rx="8" fill="#43a047"/>
  <text x="90" y="411" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">P&amp;L</text>
  <rect x="195" y="390" width="130" height="32" rx="8" fill="#43a047"/>
  <text x="260" y="411" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Combined Ratio</text>
  <rect x="480" y="335" width="130" height="32" rx="8" fill="#8e24aa"/>
  <text x="545" y="356" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Triangle</text>
  <rect x="650" y="335" width="130" height="32" rx="8" fill="#8e24aa"/>
  <text x="715" y="356" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">IBNR</text>
  <rect x="480" y="390" width="130" height="32" rx="8" fill="#8e24aa"/>
  <text x="545" y="411" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Case Reserve</text>
  <rect x="650" y="390" width="130" height="32" rx="8" fill="#8e24aa"/>
  <text x="715" y="411" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="600">Dev Factor</text>
  <line x1="155" y1="66" x2="480" y2="66" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <line x1="155" y1="121" x2="480" y2="121" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <line x1="260" y1="137" x2="90" y2="335" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <line x1="325" y1="351" x2="480" y2="351" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <line x1="90" y1="137" x2="480" y2="406" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <line x1="715" y1="367" x2="90" y2="390" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <line x1="545" y1="137" x2="260" y2="390" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" marker-end="url(#ma)"/>
  <rect x="299" y="51" width="36" height="20" rx="4" fill="#c17900"/>
  <text x="317" y="65" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">T+1</text>
  <rect x="282" y="106" width="70" height="20" rx="4" fill="#c17900"/>
  <text x="317" y="120" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">real-time</text>
  <rect x="156" y="224" width="38" height="20" rx="4" fill="#c17900"/>
  <text x="175" y="238" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">T+1</text>
  <rect x="373" y="336" width="60" height="20" rx="4" fill="#c17900"/>
  <text x="403" y="350" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">monthly</text>
  <rect x="258" y="259" width="54" height="20" rx="4" fill="#c17900"/>
  <text x="285" y="273" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">weekly</text>
  <rect x="368" y="367" width="68" height="20" rx="4" fill="#c17900"/>
  <text x="402" y="381" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">quarterly</text>
  <rect x="375" y="252" width="54" height="20" rx="4" fill="#c17900"/>
  <text x="402" y="266" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="700">annual</text>
</svg>

---

# How MeshWeaver Implements This

- **[Node Types](NodeTypeConfiguration)** — Define what a data product looks like: its properties, behavior, and layout
- **[Addressable Paths](UnifiedPath)** — Every product gets a permanent, unique address in the mesh
- **[Query Language](QuerySyntax)** — GitHub-style search syntax to discover and filter across products
- **[CRUD Operations](CRUD)** — Type-safe create, read, update, delete for any product
- **[Data Modeling](DataModeling)** — C# records as the schema contract between producers and consumers
- **[Satellite Entities](SatelliteEntities)** — Comments, approvals, access, and audit trails attach to any node
- **[Interactive Markdown](InteractiveMarkdown)** — Embed live data and charts directly inside documentation
- **[Collaborative Editing](CollaborativeEditing)** — Real-time co-editing with track changes
- **[Data Configuration](DataConfiguration)** — Wire data sources and hub-to-hub synchronization

---

# Start Building

1. **Model** — [Data Modeling](DataModeling) + [Node Type Configuration](NodeTypeConfiguration)
2. **Address** — [Unified Path](UnifiedPath)
3. **Operate** — [CRUD](CRUD) + [Data Configuration](DataConfiguration)
4. **Govern** — [Satellite Entities](SatelliteEntities)
5. **Consume** — [Interactive Markdown](InteractiveMarkdown)
