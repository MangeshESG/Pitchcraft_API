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

var builder = WebApplication.CreateBuilder(args);

// ✅ OpenAI settings
builder.Services.Configure<OpenAISettings>(
    builder.Configuration.GetSection("OpenAI"));

// ✅ Kestrel configuration
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
});

// ✅ Swagger configuration
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

// ✅ HTTP Clients
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
builder.Services.AddHttpClient<ZohoService>(client =>
{
    client.BaseAddress = new Uri("https://www.zohoapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "PitchGenApi/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
    UseDefaultCredentials = false,
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 10
});

// ✅ Database Context
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ✅ JWT Authentication
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

// ✅ CORS Policy
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

// ✅ Dependency Injection
builder.Services.AddAuthorization();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IPromptRepository, PromptRepository>();
builder.Services.AddHttpClient<IPitchService, PitchService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<EmailSendingHelper>();
builder.Services.AddScoped<EmailTemplateHelper>();
builder.Services.AddScoped<ContactRepository>();
builder.Services.AddScoped<IPitchGenDataRepository, PitchGenDataRepository>();
builder.Services.AddScoped<IRegisterEmailSender, RegisterEmailSender>();
builder.Services.AddScoped<IStripeRepository, StripeRepository>();
builder.Services.AddScoped<IResetPassworde, ResetPassword>();
builder.Services.AddSingleton<JwtService>();

// ✅ Add Background Jobs
builder.Services.AddHostedService<EmailSchedulerService>();
builder.Services.AddHostedService<MonthlyCreditResetService>();

builder.Services.AddControllers();

// ✅ Register CampaignPromptService and HttpClient
builder.Services.AddHttpClient<CampaignPromptService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});



var app = builder.Build();

// ✅ Swagger UI
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

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

// ✅ React fallback
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/swagger"))
    {
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }
    context.Response.Redirect("/");
    return Task.CompletedTask;
});

app.Run();
