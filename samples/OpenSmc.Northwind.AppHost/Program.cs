var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.OpenSmc_Northwind_Application>("opensmc-northwind-application");

builder.Build().Run();
