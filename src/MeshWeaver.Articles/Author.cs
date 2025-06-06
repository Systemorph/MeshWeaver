﻿namespace MeshWeaver.Articles;

public record Author(string FirstName, string LastName)
{
    public string MiddleName { get; init; }
    public string ImageUrl { get; init; }
}
