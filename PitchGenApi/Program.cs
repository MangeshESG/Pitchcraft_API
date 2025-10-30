using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PitchGenApi.Services;
using PitchGenApi.Repository;
using Microsoft.OpenApi.Models;
using PitchGenApi.Services;
using PitchGenApi.Model;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Authentication;
using PitchGenApi;
using PitchGenApi.Repositories;
using PitchGenApi.Helpers;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OpenAISettings>(
    builder.Configuration.GetSection("OpenAI"));

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "PitchGen API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

// 🔥 Configure HttpClient with TLS enforcement
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<CampaignPromptService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    });

builder.Services.AddHttpClient<WebSearchService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    });

// 🔥 ZOHO SERVICE - CRITICAL CONFIGURATION
builder.Services.AddHttpClient<ZohoService>(client =>
{
    client.BaseAddress = new Uri("https://www.zohoapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "PitchGenApi/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler
    {
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
        UseDefaultCredentials = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10
    };
    return handler;
});

// DB Context
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// JWT Auth
var key = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
    });

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("MyCorsPolicy", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://test.pitchkraft.ai",
                "https://test.pitchkraft.ai")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Dependency Injection
builder.Services.AddAuthorization();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IPromptRepository, PromptRepository>();
builder.Services.AddHttpClient<IPitchService, PitchService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<EmailSendingHelper>();
builder.Services.AddScoped<EmailTemplateHelper>();
builder.Services.AddHostedService<EmailSchedulerService>();
builder.Services.AddScoped<ContactRepository>();
builder.Services.AddScoped<ZohoDataService>();
builder.Services.AddScoped<IPitchGenDataRepository, PitchGenDataRepository>();
builder.Services.AddScoped<IRegisterEmailSender, RegisterEmailSender>();
builder.Services.AddScoped<IStripeRepository, StripeRepository>();
builder.Services.AddScoped<IResetPassworde, ResetPassword>();
builder.Services.AddSingleton<JwtService>();

builder.Services.AddControllers();

// ✅ Register CampaignPromptService and HttpClient
builder.Services.AddHttpClient<CampaignPromptService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddHttpClient<WebSearchService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

var app = builder.Build();

// Swagger configuration
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "PitchGen API v1");
    c.RoutePrefix = "swagger";
});

app.UseCors("MyCorsPolicy");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ✅ Serve static files from wwwroot with proper configuration
var wwwrootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
Console.WriteLine($"📁 Serving static files from: {wwwrootPath}");
Console.WriteLine($"📁 ContentRootPath: {app.Environment.ContentRootPath}");

if (Directory.Exists(wwwrootPath))
{
    var indexPath = Path.Combine(wwwrootPath, "index.html");
    Console.WriteLine($"✅ wwwroot exists. index.html exists: {File.Exists(indexPath)}");

    // Serve default files (index.html)
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        DefaultFileNames = new List<string> { "index.html" },
        FileProvider = new PhysicalFileProvider(wwwrootPath),
        RequestPath = ""
    });

    // Serve static files
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(wwwrootPath),
        RequestPath = "",
        OnPrepareResponse = ctx =>
        {
            // Add cache headers for static assets
            if (ctx.File.Name.EndsWith(".js") || ctx.File.Name.EndsWith(".css"))
            {
                ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=31536000");
            }
        }
    });
}
else
{
    Console.WriteLine($"❌ WARNING: wwwroot directory not found at {wwwrootPath}");
}

// ✅ Map API controllers FIRST before fallback
app.MapControllers();

// ✅ SPA Fallback - serve index.html for client-side routing
app.MapFallback(async context =>
{
    // Don't handle API, Swagger, or tracking requests
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/swagger") ||
        context.Request.Path.StartsWithSegments("/track"))
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Serve index.html for SPA routing
    var indexPath = Path.Combine(wwwrootPath, "index.html");
    if (File.Exists(indexPath))
    {
        Console.WriteLine($"🔄 Serving index.html for path: {context.Request.Path}");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        Console.WriteLine($"❌ index.html not found at: {indexPath}");
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync($"index.html not found at {indexPath}");
    }
});

app.Run();