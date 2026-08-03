using System.Text;
using System.Text.Json;
using AgendaApi.Api.Middleware;
using AgendaApi.Application;
using AgendaApi.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Error)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Error)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Error)
    .WriteTo.Console(outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(
            Environment.GetEnvironmentVariable("LOG_PATH") ?? "./logs",
            "log-.txt"),
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Properties:j}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Layers
builder.Services.AddInfrastructureLayer(builder.Configuration);
builder.Services.AddApplicationLayer();

// Auth (JWT)
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret no est\u00e1 configurado");
var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "AgendaApi",
        ValidAudience = "AgendaApp",
        IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(jwtKey)
    };
});

builder.Services.AddMemoryCache();
builder.Services.AddControllers(options =>
{
    // Deny by default: todos los endpoints requieren autenticación
    options.Filters.Add(new AuthorizeFilter());
});
builder.Services.AddEndpointsApiExplorer();

// CORS (permitir llamadas desde frontends en desarrollo)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Forwarded headers (producción detrás de Cloudflare Tunnel / Nginx)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "AgendaApi - Agente de Citas Multi-Tenant",
        Version = "v1",
        Description = "API de agendamiento de citas multi-tenant con integraci\u00f3n WhatsApp + Google Calendar / Microsoft 365"
    });

    // Bot\u00f3n Authorize en Swagger para probar endpoints con JWT
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Ingresar el token JWT"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Forwarded headers (detrás de Cloudflare Tunnel / Nginx) — debe ser lo primero en el pipeline
// para que Request.Scheme/Host se resuelvan correctamente antes de cualquier middleware.
app.UseForwardedHeaders();

// Exception handler global — devuelve JSON seguro, sin stack traces en producción
app.UseExceptionHandler(appError =>
{
    appError.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (exception == null) return;

        Log.Error(exception, "[AgendaApi] Error no manejado: {Message}", exception.Message);

        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Ocurrió un error interno",
            detail = builder.Environment.IsDevelopment() ? exception.Message : null
        }));
    });
});

// Middleware pipeline
app.UseSwagger();
app.UseSwaggerUI();

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgendaApi.Infrastructure.Data.AgendaDbContext>();
    db.Database.Migrate();
    Log.Information("[AgendaApi] Migraciones aplicadas correctamente");
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<TenantEnricherMiddleware>();

app.MapControllers();

// Health check con verificación de base de datos
app.MapGet("/health", async (AgendaApi.Infrastructure.Data.AgendaDbContext db) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            database = "connected"
        });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[Health] Database health check failed");
        return Results.Json(
            new { status = "unhealthy", timestamp = DateTime.UtcNow, database = "disconnected" },
            statusCode: 503);
    }
});

Log.Information("[AgendaApi] Aplicaci\u00f3n iniciada correctamente");
app.Run();
