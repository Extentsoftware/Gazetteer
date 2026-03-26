using Gazetteer.AI.Extensions;
using Gazetteer.Api.Hubs;
using Gazetteer.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGazetteerInfrastructure(builder.Configuration);
builder.Services.AddGazetteerAI(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Gazetteer API",
        Version = "v1",
        Description = "EU & UK Gazetteer search API with Location Copilot"
    });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
    // SignalR requires credentials support
    options.AddPolicy("SignalR", policy =>
    {
        policy.SetIsOriginAllowed(_ => true).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Gazetteer API v1"));
app.UseCors();
app.MapControllers();
app.MapHub<ChatHub>("/chatHub").RequireCors("SignalR");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
