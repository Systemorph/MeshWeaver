---
Name: "Northwind Overview"
Description: "This is a sample description of the article."
Thumbnail: "images/thumbnail.jpg"
Published: "2024-09-24"
Authors:
  - "Roland Bürgi"
  - "Anna Kuleshova"
Tags:
  - "Northwind"
  - "Conceptual"
---

# Northwind

This is a model for the [Northwind Database](https://github.com/microsoft/sql-server-samples/blob/master/samples/databases/northwind-pubs/readme.md). 
It is a small data domain modelling an e-commerce store. The complexity is moderate, it is more realistic than,
e.g. a TODO application or a blog application. Nevertheless, the complexity is not too big, so that we can 
describe the basic principles of data modeling. 

## Counter

The counter doesn't really fit here, we should move it to a spearate project. 

@("ProductSummary"){ Layout = "Documentation" }

## Interactive Reporting

Make your reports interactive by using the [Interactive Reporting](InteractiveReporting.md) feature. Here an example:

@("OrderSummary"){ Layout = "Documentation" }
