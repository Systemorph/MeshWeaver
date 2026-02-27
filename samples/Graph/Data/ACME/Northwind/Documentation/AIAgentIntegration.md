---
NodeType: "ACME/Northwind/Article"
Title: "AI Agent Integration"
Abstract: "How the NorthwindAgent enables natural language analytics queries and data visualization"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "Northwind"
  - "AI"
  - "Agents"
---

The Northwind sample includes a specialized AI agent for analytics queries. The NorthwindAgent demonstrates how domain-specific agents can provide intelligent data access and visualization through natural language.

# Remote Control Philosophy

AI agents **remote control** MeshWeaver applications rather than being embedded within them. This ensures clean separation of concerns and allows agents to interact through the same message-based interfaces as human users.

For the design philosophy and benefits of this approach, see [Agentic AI Architecture](MeshWeaver/Documentation/Architecture/AgenticAI).

# NorthwindAgent Overview

The NorthwindAgent is specialized for gourmet food distribution analytics:

```markdown
---
nodeType: Agent
name: Northwind Agent
description: Analytics agent for ACME Northwind - gourmet food sales, orders, products, customers, employees, and suppliers.
icon: ChartMultiple
category: Agents
groupName: Analytics
isDefault: true
---
```

## Agent Capabilities

- Query and analyze orders, products, customers, employees, and suppliers
- Display charts and reports for sales performance and product analytics
- Compare year-over-year trends and identify patterns
- Provide customer insights and segmentation

## Business Context

The agent understands the Northwind business domain:

| Entity | Count | Examples |
|--------|-------|----------|
| Products | 77 | Across 8 categories (Beverages, Dairy, Seafood, etc.) |
| Customers | 91 | In 21 countries worldwide |
| Employees | 9 | Sales representatives and managers |
| Suppliers | 29 | From 16 countries |
| Orders | 830 | With 2,155 line items (2024-2025) |

# MeshPlugin Integration

The NorthwindAgent uses the `MeshPlugin` for data access. For the complete API reference, see [MeshPlugin Tools](MeshWeaver/Documentation/AI/Tools/MeshPlugin).

| Tool | Purpose in Northwind |
|------|---------------------|
| **Get** | Retrieve order, customer, or product details |
| **Search** | Query across entities using GitHub-style syntax |
| **NavigateTo** | Display analytics views and dashboards |
| **GetLayoutAreas** | Discover available views for a node |
| **DisplayLayoutArea** | Render charts and reports |

## Layout Area Priority

The agent prioritizes visual output over raw data:

```
CRITICAL: When users ask to view, show, list, or display analytics:
- ALWAYS prefer displaying layout areas over providing raw data as text
- First check available layout areas using GetLayoutAreas
- If an appropriate layout area exists:
  1. Call DisplayLayoutArea with the appropriate area name
  2. Provide a brief confirmation message
  3. DO NOT also output the raw data as text
```

# Natural Language Examples

## Sales Queries

**Category Analysis**:
```
User: "Show me sales by category"
Agent: [Displays SalesByCategory bar chart]
"Displayed sales by category. Beverages leads with the highest revenue."
```

**Year-over-Year Comparison**:
```
User: "Compare 2024 and 2025 sales"
Agent: [Displays SalesByCategoryComparison view]
"Displayed year-over-year comparison showing category performance across both years."
```

**Regional Breakdown**:
```
User: "Which regions have the highest sales?"
Agent: [Displays RegionalAnalysis chart]
"Displayed regional analysis. The Western region leads in sales volume."
```

## Customer Analysis

**Top Customers**:
```
User: "Who are our best customers?"
Agent: [Displays TopClients chart]
"Displayed top 5 customers by revenue. QUICK-Stop leads with the highest order value."
```

**Customer Segmentation**:
```
User: "Show customer segmentation"
Agent: [Displays CustomerSegmentation view]
"Displayed customer segments: VIP, High Value, Regular, Occasional, and New customers."
```

**Geographic Distribution**:
```
User: "Where are our customers located?"
Agent: [Displays CustomerGeographicDistribution view]
"Displayed geographic distribution across 21 countries."
```

## Product Analytics

**Top Products**:
```
User: "What are our best-selling products?"
Agent: [Displays TopProducts chart]
"Displayed top 5 products by revenue."
```

**Category Performance**:
```
User: "Show product performance by category"
Agent: [Displays ProductCategoryAnalysis pie chart]
"Displayed category breakdown. Beverages and Dairy Products account for ~40% of revenue."
```

**Product Trends**:
```
User: "Show product performance trends"
Agent: [Displays ProductPerformanceTrends line chart]
"Displayed monthly trends for top 5 products."
```

## Financial Queries

**Financial Summary**:
```
User: "What's our financial summary?"
Agent: [Displays FinancialSummary view]
"Displayed key metrics: total revenue, order count, products sold, and discount analysis."
```

**Discount Analysis**:
```
User: "How effective are our discounts?"
Agent: [Displays DiscountAnalysisReport]
"Displayed discount effectiveness report with recommendations."
```

**Monthly Breakdown**:
```
User: "Show monthly financial breakdown"
Agent: [Displays MonthlyBreakdownTable]
"Displayed monthly breakdown with revenue, orders, averages, and freight costs."
```

## Employee Performance

**Top Performers**:
```
User: "Who are our top sales representatives?"
Agent: [Displays TopEmployees chart]
"Displayed top 5 employees by revenue. Nancy Davolio leads in sales performance."
```

**Performance Report**:
```
User: "Generate employee performance report"
Agent: [Displays TopEmployeesReport]
"Displayed performance insights with key metrics and observations."
```

# Available Layout Areas

The agent can display any of the 53 available views:

## Dashboard
- **Dashboard**: Main overview with 4-panel grid

## Sales Analytics
- **SalesByCategory**: Revenue by product category
- **SalesGrowthSummary**: Growth metrics and indicators
- **SalesByCategoryComparison**: Multi-year comparison
- **SalesByCategoryWithPrevYear**: Current vs previous year
- **CountrySalesComparison**: Top countries by sales
- **RegionalAnalysis**: Sales by region

## Orders
- **OrderSummary**: Top 5 orders by value
- **OrdersCount**: Monthly order counts
- **AvgOrderValue**: Average order value trend
- **MonthlyOrdersTable**: Monthly summary table
- **OrderDetailsReport**: Detailed statistics
- **OrdersSummaryReport**: Comprehensive report

## Products
- **ProductOverview**: Product performance grid
- **TopProducts**: Top 5 products chart
- **TopProductsByCategory**: Top 10 by category
- **ProductCategoryAnalysis**: Revenue pie chart
- **ProductPerformanceTrends**: Monthly trends
- **ProductDiscountImpact**: Discount impact analysis
- **ProductSalesVelocity**: Turnover metrics

## Customers
- **CustomerSummary**: Customer metrics grid
- **TopClients**: Top 5 customers chart
- **CustomerOrderFrequency**: Frequency distribution
- **CustomerGeographicDistribution**: Geographic map
- **CustomerLifetimeValue**: CLV analysis
- **CustomerSegmentation**: VIP/Regular/New segments
- **CustomerRetentionAnalysis**: Retention metrics
- **CustomerPurchaseBehavior**: Purchase patterns

## Financial
- **FinancialSummary**: Key financial metrics
- **RevenueSummary**: Monthly revenue trend
- **DiscountSummary**: Discount by category
- **DiscountVsRevenue**: Discount impact
- **MonthlyBreakdownTable**: Full monthly breakdown
- **DiscountPercentage**: Discount distribution
- **DiscountAnalysisReport**: Performance overview

# Architecture Benefits

## Domain Expertise

The NorthwindAgent provides context that generic agents lack:
- **Product Categories**: Understands Beverages, Dairy Products, Seafood, etc.
- **Business Metrics**: Knows about revenue, AOV, discount rates, freight
- **Time Periods**: Handles year filtering and comparisons
- **Key Entities**: Recognizes customers, employees, suppliers

## View Selection

The agent intelligently selects appropriate views:
- Sales queries → SalesByCategory, SalesGrowthSummary
- Customer queries → TopClients, CustomerSegmentation
- Product queries → ProductOverview, TopProducts
- Financial queries → FinancialSummary, DiscountAnalysisReport

## Consistent Experience

Users get visual output instead of raw data:
- Charts for comparisons and trends
- Data grids for detailed exploration
- Markdown reports for summaries

# Cross-References

For related documentation:

- **[MeshPlugin Tools](MeshWeaver/Documentation/AI/Tools/MeshPlugin)**: Universal data access tools
- **[Agentic AI Architecture](MeshWeaver/Documentation/Architecture/AgenticAI)**: Agent design patterns
- **[Northwind Architecture](Northwind/Documentation/Architecture)**: Data model and views
- **[Unified References](Northwind/Documentation/UnifiedReferences)**: All addressable paths

# Conclusion

The NorthwindAgent demonstrates how domain-specific AI agents can enhance analytics applications:

1. **Natural Language**: Users query data conversationally
2. **Visual Output**: Charts and reports instead of raw data
3. **Domain Context**: Business-aware query understanding
4. **View Selection**: Intelligent layout area choices
5. **Year Filtering**: Time-aware comparisons

This pattern enables sophisticated analytics experiences while maintaining clean separation between AI capabilities and core application logic.
