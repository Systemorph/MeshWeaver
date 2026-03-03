---
NodeType: "FutuRe/Report"
Title: "FutuRe Group Annual Profitability Report"
Abstract: "Comprehensive profitability analysis across EuropeRe and AmericasIns business units, covering 10 group lines of business over an 18-month rolling window. Includes key performance indicators, line-of-business breakdown, loss ratios, estimate-vs-actual tracking, and quarterly trends."
Icon: "DocumentText"
Published: "2026-03-01"
Authors:
  - "Roland Buergi"
Tags:
  - "Profitability"
  - "Annual Report"
  - "Insurance"
  - "Loss Ratio"
  - "Business Units"
---

This report provides a consolidated view of the FutuRe Group profitability across both business units — **EuropeRe** (EMEA, EUR) and **AmericasIns** (Americas, USD). All figures are sourced from the virtual data cube, which applies TransactionMapping percentage splits to transform local business unit data into group-level lines of business without copying any data.

## Key Performance Indicators

<div style="display: flex; gap: 20px; margin: 20px 0;">
  <div style="flex: 1;">

@@("FutuRe/Profitability/KeyMetrics")

  </div>
  <div style="flex: 1;">

@@("FutuRe/Profitability/ProfitByLoB")

  </div>
</div>

## Monthly Profitability Overview

The chart below shows the monthly P&L waterfall — premium income (positive) stacked against claims and cost components (negative), with a net profit line overlay.

@@("FutuRe/Profitability/ProfitabilityOverview")

## Line of Business Breakdown

The table summarises estimated premium, claims, operating costs, net profit, and loss ratio for each group line of business across the full 18-month window.

@@("FutuRe/Profitability/ProfitabilityTable")

## Loss Ratios

Loss ratio (Claims / Premium) is the primary underwriting performance metric. A ratio above 100% indicates an underwriting loss on that line. The chart below compares loss ratios across all group lines of business.

@@("FutuRe/Profitability/LossRatio")

## Quarterly Trend

Quarterly aggregation smooths monthly volatility and reveals seasonal patterns. The chart compares actual computed profit against expected profit budgets.

@@("FutuRe/Profitability/QuarterlyTrend")

## Estimate vs Actual

For amount types that track actuals (Premium, Claims, External Cost), the section below shows a month-by-month comparison table and a Premium estimate-vs-actual chart.

@@("FutuRe/Profitability/EstimateVsActual")

## Conclusion

The FutuRe Group maintains a healthy combined ratio below 100% across the majority of its lines of business. The virtual transformation approach ensures that profitability analysis reflects the latest local business unit data in real time, without any physical data movement or duplication.
