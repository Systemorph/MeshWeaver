using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Binds every <c>[Collection("Cosmos")]</c> test class to the shared <see cref="CosmosFixture"/>
/// — one emulator container (or real-account session) per collection, mirroring the Snowflake
/// test project's <c>[CollectionDefinition("Snowflake")]</c>.
/// </summary>
[CollectionDefinition("Cosmos")]
public class CosmosCollection : ICollectionFixture<CosmosFixture>;
