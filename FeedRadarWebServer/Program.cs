using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Railway 透過 PORT 環境變數指定 port
var port = Environment.GetEnvironmentVariable("PORT") ?? "5062";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ProductRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();
app.Run();
