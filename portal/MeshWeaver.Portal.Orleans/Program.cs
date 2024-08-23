using MeshWeaver.Hosting;
var builder = WebApplication.CreateBuilder(args);
builder.AddKeyedRedisClient("hubs-redis");
builder.AddServiceDefaults();
builder.UseOrleans(orleansBuilder =>
{
    if (builder.Environment.IsDevelopment())
    {
        orleansBuilder.ConfigureEndpoints(Random.Shared.Next(10_000, 50_000), Random.Shared.Next(10_000, 50_000));
    }
});
var app = builder.Build();

app.Run();

