---
NodeType: "Organization"
Title: "FutuRe Group Annual Profitability Report"
Icon: /static/storage/content/FutuRe/icon.svg
Abstract: "Comprehensive profitability analysis across EuropeRe and AmericasIns business units covering 10 group lines of business over an 18-month rolling window."
Tags:
  - "Profitability"
  - "Annual Report"
  - "Insurance"
  - "Loss Ratio"
  - "Business Units"
---

This report provides a consolidated view of the FutuRe Group profitability across all business units — **EuropeRe** (EMEA, EUR), **AmericasIns** (Americas, USD), and **AsiaRe** (Asia-Pacific, JPY). All figures are converted to the group reporting currency **CHF** using exchange rates maintained in the ExchangeRate reference data. Estimated amounts are converted at **plan rates** (fixed at budget time), while actual amounts use **actual rates** (market rates when transactions occurred). The variance (Actual − Estimate) therefore captures both operational performance differences and FX effects. The virtual data cube applies TransactionMapping percentage splits to transform local business unit data into group-level lines of business without copying any data.

---

## Key Performance Indicators

The KPIs below summarise total premium, claims, profitability ratios, and portfolio scope across all business units.

@@("FutuRe/Analysis/KeyMetrics")

---

## Profit by Line of Business

The chart below ranks each group line of business by net profit (Premium minus all cost components) across the full 18-month window.

@@("FutuRe/Analysis/ProfitByLoB")

---

## Monthly Profitability Overview

The chart below shows the monthly P&L waterfall — premium income (positive) stacked against claims and cost components (negative), with a net profit line overlay.

@@("FutuRe/Analysis/ProfitabilityOverview")

---

## Line of Business Breakdown

The table summarises estimated premium, claims, operating costs, net profit, and loss ratio for each group line of business across the full 18-month window.

@@("FutuRe/Analysis/ProfitabilityTable")

---

## Loss Ratios

Loss ratio (Claims / Premium) is the primary underwriting performance metric. A ratio above 100 % indicates an underwriting loss on that line. The chart below compares loss ratios across all group lines of business.

@@("FutuRe/Analysis/LossRatio")

---

## Quarterly Trend

Quarterly aggregation smooths monthly volatility and reveals seasonal patterns. The chart compares actual computed profit against expected profit budgets.

@@("FutuRe/Analysis/QuarterlyTrend")

---

## Annual Profitability Waterfall

The waterfall chart below shows how total premium flows through claims and cost components to arrive at net profit.

@@("FutuRe/Analysis/AnnualProfitabilityWaterfall")

---

## Estimate vs Actual

For amount types that track actuals (Premium, Claims, External Cost), the section below shows a month-by-month comparison table and a Premium estimate-vs-actual chart.

@@("FutuRe/Analysis/EstimateVsActual")

---

## Conclusion

The FutuRe Group maintains a healthy combined ratio below 100 % across the majority of its lines of business. The virtual transformation approach ensures that profitability analysis reflects the latest local business unit data in real time, without any physical data movement or duplication.
