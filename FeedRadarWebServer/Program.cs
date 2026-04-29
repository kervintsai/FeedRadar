using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// 只在 Railway 容器內（有設定 PORT）才覆蓋監聽位置
var railwayPort = Environment.GetEnvironmentVariable("PORT");
if (railwayPort != null)
    builder.WebHost.UseUrls($"http://0.0.0.0:{railwayPort}");

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ProductRepository>();

var app = builder.Build();

Console.WriteLine($"[Env] DATABASE_URL set={!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DATABASE_URL"))}");
Console.WriteLine($"[Env] PORT={Environment.GetEnvironmentVariable("PORT") ?? "(not set)"}");
Console.WriteLine($"[Env] ASPNETCORE_ENVIRONMENT={Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "(not set)"}");

app.UseForwardedHeaders();

app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode  = 500;
    var body = new ApiErrorResponse(false, new ApiError("SERVER_ERROR", "Internal server error"));
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
}));

app.MapOpenApi();
app.MapScalarApiReference();

if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();
app.Run();
