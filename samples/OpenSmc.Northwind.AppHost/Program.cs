/// <summary>
/// This file contains the main entry point for the application. 
/// It is responsible for creating, configuring, and running the distributed application.
/// </summary>
/// <remarks>
/// The application is configured with a specific project, identified by the "Projects.OpenSmc_Northwind_Application" class, which is likely to contain specific configurations, services, and startup routines for the application related to the "opensmc-northwind-application".
/// </remarks>
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.OpenSmc_Northwind_Application>("opensmc-northwind-application");

builder.Build().Run();
