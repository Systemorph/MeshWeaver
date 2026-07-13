using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Binds every <c>[Collection("Snowflake")]</c> test class to the shared
/// <see cref="SnowflakeFixture"/> — one emulator container (or real-account session) per
/// collection, mirroring the PG test project's <c>[CollectionDefinition("PostgreSql")]</c>.
/// </summary>
[CollectionDefinition("Snowflake")]
public class SnowflakeCollection : ICollectionFixture<SnowflakeFixture>;
