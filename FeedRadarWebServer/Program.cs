using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// 只在 Railway 容器內（有設定 PORT）才覆蓋監聽位置
var railwayPort = Environment.GetEnvironmentVariable("PORT");
if (railwayPort != null)
    builder.WebHost.UseUrls($"http://0.0.0.0:{railwayPort}");

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ProductRepository>();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();
app.Run();
