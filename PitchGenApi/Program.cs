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
using System.Net.Security;
using System.Net.Sockets;
using PitchGenApi;
using PitchGenApi.Repositories;
using PitchGenApi.Helpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using PitchGenApi.Middleware;
using Serilog;
using Serilog.Events;

using static PitchGenApi.Services.CampaignPromptService;

var builder = WebApplication.CreateBuilder(args);

// ===============================
// Logging
// ===============================
// The Serilog packages were referenced and appsettings carried a Serilog
// section, but nothing ever called UseSerilog -- so unhandled exceptions were
// written nowhere and a 500 on the server told us only that it was a 500. The
// sink is configured here rather than from appsettings because the server
// keeps its own copy of that file, and because the path has to be absolute:
// under IIS the working directory is not the app folder, so a relative path
// lands somewhere nobody looks.
var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs");
Directory.CreateDirectory(logDirectory);

builder.Host.UseSerilog((context, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .WriteTo.File(
        Path.Combine(logDirectory, "error-.txt"),
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: LogEventLevel.Error,
        retainedFileCountLimit: 31,
        shared: true));

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
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null
            );
            sqlOptions.CommandTimeout(180);
        }));

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
builder.Services.AddScoped<IReplyEmailRepository, ReplyEmailRepository>();
builder.Services.AddHttpClient<IContactQAService, ContactQAService>();
builder.Services.AddScoped<IForwardRepository, ForwardRepository>();
builder.Services.AddScoped<IExtensionRepository, ExtensionRepository>();
// No typed HttpClient: the profile summary now goes through IPitchService /
// DeepSeekPitchService instead of calling OpenAI directly.
builder.Services.AddScoped<IExtensionProfileService, ExtensionProfileService>();
builder.Services.AddScoped<CalculateEmailRepository>();
builder.Services.AddScoped<DefaultCustomFieldSeeder>();

// Admin-controlled model per AI purpose (Settings > AI models)
builder.Services.AddScoped<IAiModelSettingsService, AiModelSettingsService>();

// Admin-controlled security switches, e.g. the login OTP requirement
// (Settings > Security)
builder.Services.AddScoped<ISecuritySettingsService, SecuritySettingsService>();

// Admin-editable AI instructions, e.g. the extension's email research prompt
// (Settings > Admin > Prompts)
builder.Services.AddScoped<IPromptSettingsService, PromptSettingsService>();

// Audience Assurance tuning, e.g. contacts per model request
// (Settings > Admin > Validation)
builder.Services.AddScoped<IValidationSettingsService, ValidationSettingsService>();

// Per-contact personalization inputs shared by email and LinkedIn generation
builder.Services.AddScoped<IContactPromptContextService, ContactPromptContextService>();

// Hunter.io, the stage after the AI email search. Its own timeout sits just
// above the max_duration the service asks Hunter for, so a slow lookup ends as
// a skipped stage rather than holding the unlock open.
builder.Services.AddHttpClient<IHunterEmailService, HunterEmailService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

// Prospeo, the first stage of both the extension unlock and the Audience
// Assurance email check.
builder.Services.AddHttpClient<IProspeoEmailService, ProspeoEmailService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Audience Assurance. A batch of 50 contacts with web search enabled routinely
// runs for minutes, so this client's timeout is far longer than the others —
// it is the background worker waiting, not a user.
builder.Services.AddHttpClient<IContactValidationService, ContactValidationService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});

builder.Services.Configure<DeepSeekSettings>(
    builder.Configuration.GetSection("DeepSeekSettings"));

// api.deepseek.com refuses anything below TLS 1.2, and on Windows Server the
// default handler lets schannel pick the protocol - which is how the same
// build reaches OpenAI fine but fails DeepSeek with "The SSL connection could
// not be established". Pinned the way CampaignPromptService and ZohoService
// already are.
builder.Services.AddHttpClient<DeepSeekPitchService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    });

// Qwen, via Alibaba Cloud Model Studio's OpenAI-compatible endpoints.
builder.Services.Configure<QwenSettings>(
    builder.Configuration.GetSection("QwenSettings"));

// Same TLS pin as DeepSeek above, for the same reason: the Model Studio hosts
// refuse anything below TLS 1.2, and on Windows Server the default handler
// leaves the protocol to schannel - which is how one build can reach OpenAI
// fine and fail everything else with "The SSL connection could not be
// established".
// IPv4 only, and that is not a preference - it is the difference between a
// request taking one second and taking forty-three.
//
// The Model Studio regional hosts advertise AAAA records. Measured 2026-09-16
// against the Frankfurt workspace host: DNS returns two IPv6 addresses and two
// IPv4 addresses, the IPv4 connect completes in 0.20s, and each IPv6 connect
// blackholes and times out after 21s. .NET works through the list in order, so
// every single call paid 2 x 21s before it ever reached the IPv4 address -
// which is the whole of the "timed out after 180 seconds" failure, with the
// retry loop on top. Nothing to do with the model, the search, or thinking
// mode: a bare TCP connect to the host measured 42.73s while the API call that
// followed it took about a second.
//
// This machine and the UK server both have IPv6 addresses that are link-local
// only, so neither has a route. Pinning the family here fixes both without
// depending on host network configuration.
builder.Services.AddHttpClient<QwenPitchService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Same TLS pin as DeepSeek below; SocketsHttpHandler spells it
        // differently from HttpClientHandler but means the same thing.
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        },

        ConnectCallback = async (context, cancellationToken) =>
        {
            // AddressFamily.InterNetwork makes ConnectAsync consider only the
            // IPv4 records the hostname resolves to; the AAAA answers are never
            // attempted.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });


builder.Services.AddSingleton<JwtService>();

// ===============================
// ✅ Background Jobs
// ===============================
// Singleton so the runner writes to the same instance the status endpoint
// reads. Registered before the hosted service that depends on it.
builder.Services.AddSingleton<PitchGenApi.Background.ValidationRunnerDiagnostics>();
builder.Services.AddHostedService<BackgroundWorkerService>();
builder.Services.AddScoped<IInboxRefreshJob, InboxRefreshJob>();

builder.Services.AddControllers();

// ===============================
// 🚀 Build App
// ===============================
var app = builder.Build();

// ===============================
// Unhandled exceptions
// ===============================
// First in the pipeline so it wraps everything below it. Without this the
// class was dead code: exceptions escaped to IIS, which answered with a bare
// 500 carrying no clue what went wrong.
app.UseMiddleware<GlobalExceptionMiddleware>();

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
// ✅ Ensure the static-file folders exist
// ===============================
// ContentRootPath rather than Directory.GetCurrentDirectory(): under IIS the
// working directory is not reliably the app folder. And PhysicalFileProvider
// throws DirectoryNotFoundException on a missing path, which kills startup
// before the process ever listens — an ANCM 502.5 with nothing else to go on.
// Publish drops empty folders, so email-attachments has to be created here the
// same way uploads always was.
var webRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var uploadsPath = Path.Combine(webRootPath, "uploads");
var emailAttachmentsPath = Path.Combine(webRootPath, "email-attachments");

Directory.CreateDirectory(webRootPath);
Directory.CreateDirectory(uploadsPath);
Directory.CreateDirectory(emailAttachmentsPath);

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
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

// ✅ Serve /email-attachments publicly
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(emailAttachmentsPath),
    RequestPath = "/email-attachments"
});

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();

// Serve normal wwwroot files
app.UseStaticFiles();

app.MapControllers();

// React SPA fallback
app.MapFallbackToFile("index.html");

app.Run();
