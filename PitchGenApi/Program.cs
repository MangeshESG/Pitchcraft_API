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
using PitchGenApi;
using PitchGenApi.Middleware;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ===== Serilog =====
Log.Logger = new LoggerConfiguration()
    .WriteTo.File("logs/error-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ===== Configurations =====
builder.Services.Configure<OpenAISettings>(
    builder.Configuration.GetSection("OpenAI")
);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "PitchGen API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: 'Bearer {token}'",
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

// ===== DbContext =====
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
);

// ===== JWT Auth =====
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

// ===== CORS =====
builder.Services.AddCors(options =>
{
    options.AddPolicy("MyCorsPolicy", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://test.pitchkraft.ai"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ===== Dependencies =====
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IPromptRepository, PromptRepository>();
builder.Services.AddScoped<IPitchService, PitchService>();
builder.Services.AddScoped<EmailSendingHelper>();
builder.Services.AddHostedService<EmailSchedulerService>();
builder.Services.AddScoped<ContactRepository>();
builder.Services.AddScoped<IPitchGenDataRepository, PitchGenDataRepository>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<IResetPassworde, ResetPassword>();
builder.Services.AddHttpClient<ZohoService>(client =>
{
    client.BaseAddress = new Uri("https://www.zohoapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<CampaignPromptService>();
builder.Services.AddHttpClient<WebSearchService>();

var app = builder.Build();

// ===== Global Exception Middleware =====
app.UseMiddleware<GlobalExceptionMiddleware>();

// ===== Swagger (must be first before static) =====
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "PitchGen API v1");
    c.RoutePrefix = "swagger"; // accessible at /swagger
});

// ===== CORS + Auth =====
app.UseCors("MyCorsPolicy");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ===== React static files AFTER Swagger =====
app.UseDefaultFiles();
app.UseStaticFiles();

// ===== API routes =====
app.MapControllers();

// ===== (Optional) fallback only for React, not for Swagger =====
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
