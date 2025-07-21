# What is a Data Mesh?

Data mesh is a sociotechnical approach to building a decentralized data architecture by leveraging a domain-oriented, self-serve design (in a software development perspective), and borrows Eric Evans' theory of domain-driven design and Manuel Pais' and Matthew Skelton's theory of team topologies. Data mesh mainly concerns itself with the data itself, taking the data lake and the pipelines as a secondary concern. The main proposition is scaling analytical data by domain-oriented decentralization. With data mesh, the responsibility for analytical data is shifted from the central data team to the domain teams, supported by a data platform team that provides a domain-agnostic data platform. This enables a decrease in data disorder or the existence of isolated data silos, due to the presence of a centralized system that ensures the consistent sharing of fundamental principles across various nodes within the data mesh and allows for the sharing of data across different areas. ([Wikipedia](https://en.wikipedia.org/wiki/Data_mesh)).

## Main Characteristics

1. **Decentralized Organization**: The Mesh is organized decentrally, i.e. teams can autonomously publish and consume data without involving central teams.
2. **Data as a Product**: Data is treated as a product with Service Level Objectives (SLO).
3. **Self-Service Discovery**: Consumers can discover data and consume under the SLOs.

## Sharing Views, Not Just Data

We believe that not only data should be shared but also entire views. In most cases, the views are non-trivial and do not just visualize data as is but are the product of many data points and entities with non-trivial business rules. Everyone who has worked in disciplines close to finance knows that even presumably easy concepts such as foreign exchange conversions are actually rocket science, and it is not easy to get them right, let alone do them consistently across the enterprise.

Furthermore, every data owner should own the views to be shared. Especially in disciplines involving mathematical modeling, numbers can be reported which were not calibrated. The modeler should control which views are shared and which are not.

As an example, in risk the expected value must be modeled along with the distributions. However, the expected value is not subject to risk management. Rather it is subtracted from the risk. Thus it is not appropriate to report on expected values, even though this is technically possible.

## Layout Areas in MeshWeaver

Layout areas can be easily accessed using the layout area control:

```csharp
LayoutArea(new ApplicationAddress("My Application"), "My Area")
```

The available layout areas can be browsed using the user interface.

### Example: Northwind Layout Areas

Here are some examples of shared layout areas from the Northwind application:

**Product Overview:**
```csharp
LayoutArea(new ApplicationAddress("Northwind"), "ProductOverview")
```

**Order Summary:**
```csharp
LayoutArea(new ApplicationAddress("Northwind"), "OrderSummary")
.WithStyle(style => style.WithHeight("300px"))
```

## Benefits of MeshWeaver's Approach

### 1. Domain Ownership
- Each domain team owns their data and views
- Business logic stays with the domain experts
- Consistent implementation across the enterprise

### 2. Reusable Components
- Views can be embedded in multiple applications
- Standardized calculations and business rules
- Reduced duplication and inconsistency

### 3. Self-Service Analytics
- Teams can discover and consume views independently
- No central bottleneck for data access
- Service Level Objectives ensure reliability

### 4. Interactive Notebooks
- Real data with real business logic in notebooks
- Interactive exploration and validation
- Collaborative analysis between technical and business users

## Technical Implementation

MeshWeaver implements data mesh principles through:

- **Application Addresses**: Unique identifiers for domain applications
- **Layout Areas**: Shareable views with business logic
- **Mesh Routing**: Automatic discovery and routing of data requests
- **Reactive Streams**: Real-time updates across the mesh
- **Service Level Objectives**: Monitoring and reliability guarantees

## Getting Started

To connect to a MeshWeaver data mesh:

1. **Install the connection package:**
   ```
   #r "nuget:MeshWeaver.Connection.Notebook, 2.0.0-preview1"
   ```

2. **Connect to the mesh:**
   ```
   #!connect mesh https://localhost:65260/kernel --kernel-name mesh
   ```

3. **Import necessary namespaces:**
   ```csharp
   using MeshWeaver.Layout;
   using MeshWeaver.Mesh;
   using static MeshWeaver.Layout.Controls;
   using Microsoft.DotNet.Interactive.Formatting;
   ```

4. **Access your mesh address:**
   ```csharp
   Mesh.Address
   ```

5. **Explore available layout areas through the UI or programmatically access them:**
   ```csharp
   LayoutArea(new ApplicationAddress("YourApp"), "YourArea")
   ```

This approach enables true data mesh principles where data and views are owned by domain experts but can be safely shared and consumed across the enterprise.
