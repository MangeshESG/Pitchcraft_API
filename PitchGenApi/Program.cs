using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PitchGenApi.Services;
using PitchGenApi.Repository;
using Microsoft.OpenApi.Models;
using PitchGenApi.Model;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Authentication;
using PitchGenApi;
using PitchGenApi.Repositories;
using PitchGenApi.Helpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;

using static PitchGenApi.Services.CampaignPromptService;

var builder = WebApplication.CreateBuilder(args);

// ===============================
// ✅ OpenAI settings
// ===============================
builder.Services.Configure<OpenAISettings>(
    builder.Configuration.GetSection("OpenAI"));

// ===============================
// ✅ Kestrel configuration
// ===============================
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // ✅ upload limit
});

// ===============================
// ✅ Multipart upload limits
// ===============================
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB
});

// ===============================
// ✅ Swagger configuration
// ===============================
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
            Array.Empty<string>()
        }
    });
});

// ===============================
// ✅ HTTP Clients
// ===============================
builder.Services.AddHttpClient();

builder.Services.AddHttpClient<CampaignPromptService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    });

builder.Services.AddHttpClient<ZohoService>(client =>
{
    client.BaseAddress = new Uri("https://www.zohoapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "PitchGenApi/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    UseDefaultCredentials = false,
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 10
});

// ===============================
// ✅ Database Context
// ===============================
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ===============================
// ✅ JWT Authentication
// ===============================
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

// ===============================
// ✅ CORS Policy
// ===============================
builder.Services.AddCors(options =>
{
    options.AddPolicy("MyCorsPolicy", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://app.pitchkraft.ai",
                "https://app.pitchkraft.ai")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ===============================
// ✅ Dependency Injection
// ===============================
builder.Services.AddAuthorization();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IPromptRepository, PromptRepository>();

builder.Services.AddScoped<ICompanyAlertService, CompanyAlertService>();


builder.Services.AddHttpClient<IPitchService, PitchService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddScoped<EmailSendingHelper>();
builder.Services.AddScoped<EmailTemplateHelper>();
builder.Services.AddScoped<ContactRepository>();
builder.Services.AddScoped<IPitchGenDataRepository, PitchGenDataRepository>();
builder.Services.AddScoped<IDomainVerificationRepository, DomainVerificationRepository>();
builder.Services.AddScoped<IRegisterEmailSender, RegisterEmailSender>();
builder.Services.AddScoped<IStripeRepository, StripeRepository>();
builder.Services.AddScoped<IResetPassworde, ResetPassword>();
builder.Services.AddScoped<INoteRepository, NoteRepository>();
builder.Services.AddScoped<IAttachmentRepository, AttachmentRepository>();
builder.Services.AddScoped<IInboxRepository, InboxRepository>();
builder.Services.AddScoped<IInboxEmailSyncService, InboxEmailSyncService>();
builder.Services.AddScoped<IInboxEmailService, InboxEmailService>();
builder.Services.AddScoped<IOAuthRepository, OAuthRepository>();

builder.Services.AddSingleton<JwtService>();

// ===============================
// ✅ Background Jobs
// ===============================
builder.Services.AddHostedService<BackgroundWorkerService>();

builder.Services.AddControllers();

// ===============================
// 🚀 Build App
// ===============================
var app = builder.Build();

// ===============================
// ✅ REQUIRED for production (reverse proxy)
// ===============================
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost
});

// ===============================
// ✅ Ensure wwwroot/uploads exists
// ===============================
var webRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
var uploadsPath = Path.Combine(webRootPath, "uploads");

if (!Directory.Exists(webRootPath))
    Directory.CreateDirectory(webRootPath);

if (!Directory.Exists(uploadsPath))
    Directory.CreateDirectory(uploadsPath);

// ===============================
// ✅ Swagger
// ===============================
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "PitchGen API v1");
    c.RoutePrefix = "swagger";
});

// ===============================
// ✅ Middleware pipeline
// ===============================
app.UseCors("MyCorsPolicy");
app.UseHttpsRedirection();

// Serve /uploads publicly
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads")
    ),
    RequestPath = "/uploads"
});

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// React SPA fallback
app.MapFallbackToFile("index.html");

app.Run();
