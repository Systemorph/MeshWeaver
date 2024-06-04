﻿using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Supplier(
    [property: Key] int SupplierId,
    string CompanyName,
    string ContactName,
    string ContactTitle,
    string Address,
    string City,
    string Region,
    string PostalCode,
    string Country,
    string Phone,
    string Fax,
    string HomePage
);