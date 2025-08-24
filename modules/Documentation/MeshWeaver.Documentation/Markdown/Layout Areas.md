---
Title: "Personalize Data Reports with Layout Areas"
Abstract: >
  We live in times of hyper personalization of all information. 
  This is also true for data reports: Very often, the same data is presented in different ways to different audiences.
  This article shows how to use layout areas to personalize data reports.
Thumbnail: "images/Layout Areas.jpeg"
VideoUrl: "https://www.youtube.com/embed/q1MKJ-8R2yM?si=dLCU50EDhYvELID3"
VideoDuration: "00:15:00"
Published: "2025-04-21"
Authors:
  - "Roland Bürgi"
Tags:
  - "Documentation"
  - "Layout"
  - "Layout Area"
  - "Markdown"
---

The key idea behind Layout Areas is that we should not just share data but
entire reports. The complexity in creating reports is not only accessing the data,
but rather a vast set of business logic which needs to be applied. Even seemingly
simple problems, such as foreign exchange conversion, is highly non-trivial
when it comes to the implementation details. There should be clear responsibilities
and update cycles for each table, chart, and figure, and these layout areas
should be readily consumable for our own reports.

In Interactive Markdown, we have a simple syntax for including layout areas. In this area, we include 
one of the reports of Northwind:

```
@("app/Northwind/AnnualReportSummary?Year=2023")
```
resulting in 

@("app/Northwind/AnnualReportSummary?Year=2023")

The pattern for referencing layout areas is simple. It consists of 

```
@("{addressType}/{addressId}/{area}/{**id}")
```

An alternative syntax is to put it in a layout code block:
```layout --show-header
app/Northwind/AnnualReportSummary?Year=2023
```

The same can be achieved programmatically by instantiating a LayoutArea control:

```csharp --render LayoutArea --show-code
using static MeshWeaver.Layout.Controls;
using MeshWeaver.Mesh;
LayoutArea(new ApplicationAddress("Northwind"), "TopClients")
```

For each address, you can list all layout areas. This is a standard layout
area which ships with Mesh Weaver and can reached under the `LayoutAreas` area
name. Follow here to see the [Northwind Areas](/app/Northwind/LayoutAreas).

Mesh Weaver ships with a variety of standard layout areas, as most of the parts
required for Create Read Update and Delete (CRUD) can be fully standardised. For instance,
we can visualize the data model under the area `DataModel`. See here the 
[Northwind Data Model](/app/Northwind/DataModel).

For each type in the data model, you can access its catalog using the pattern
`{addressType}/{addressId}/Catalog/{TypeName}`:

```csharp --render Catalog --show-code
LayoutArea(new ApplicationAddress("Northwind"), "Catalog", "Territory")
```

We can see that all the catalogs are nice data grids, which can, e.g., be paged and sorted. Also, we link all
the documentation wherever such properties are visible, so that you cannot only see the data itself but also all 
the definitions of the fields and types. Such functionality
which is non-trivial to implement and takes normally a large part of projects to build.

Furthermore, we have standard views for
single instances in form of `{addressType}/{addressId}/Details/{TypeName}/{Id}`. Example for a territory:

```csharp --render Details --show-code
LayoutArea(new ApplicationAddress("Northwind"), "Details", "Territory/06897")
```

Please note that data is editable wherever the user has edit rights. This means, that 
data can be literally updated from anywhere where it is visible. Obviously, edit rights can be controlled.
