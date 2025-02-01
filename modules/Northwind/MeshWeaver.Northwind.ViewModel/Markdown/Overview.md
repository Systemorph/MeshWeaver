---
Title: "Northwind"
Abstract: >
  We have modeled the [Northwind Database](https://github.com/microsoft/sql-server-samples/blob/master/samples/databases/northwind-pubs/readme.md)
  as an example for how to build views in a classical data cube scenario.
Thumbnail: "images/Northwind.png"
Published: "2025-01-31"
Authors:
  - "Roland Bürgi"
Tags:
  - "Northwind"
  - "Conceptual"
---

This is a model for the [Northwind Database](https://github.com/microsoft/sql-server-samples/blob/master/samples/databases/northwind-pubs/readme.md). 
It is a small data domain modelling an e-commerce store. The complexity is moderate, however it is more realistic than,
e.g. a TODO application or a blog application.

The Northwind Database is ideal to explain basic concepts around data modelling and
creating views and interactive articles. We have updated the data only so slightly
as to move the dates from the 90ies to the 2020s.

For the first time ever, we have created an annual report of Northwind, and
we demonstrate our ideas to create interactive articles to report and explain data.
Here is the major dashboard:

@("AnnualReportSummary"){ Id = "?Year=2023" }
The counter doesn't really fit here, we should move it to a spearate project. 

Please have a look at the [Northwind Articles](/articles/Northwind) for a detailed look.