using DashTudo.Web.Components;
using DashTudo.Web.Components.Account;
using DashTudo.Web.Data;
using DashTudo.Web.Services;
using DashTudo.Web.Services.Ai;
using DashTudo.Web.Services.Analysis;
using DashTudo.Web.Services.Parsing;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024); // uploads usam stream, não mensagens grandes

// ---------- Banco de dados (MySQL) ----------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' não configurada.");
builder.Services.AddDbContextFactory<AppDbContext>(o => o
    .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
// O Identity precisa de um DbContext "scoped"; reaproveita a mesma configuração da factory.
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// ---------- Autenticação (ASP.NET Core Identity + cookie) ----------
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = IdentityConstants.ApplicationScheme;
        o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/login";
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Cookie.Name = "dashtudo.auth";
});
builder.Services.AddAuthorization();

builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Chaves que protegem o cookie de login: persistidas em disco para sobreviver a reinícios/deploys.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("DashTudo");
}

// ---------- Serviços da aplicação ----------
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Upload"));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.AddSingleton<FileParserService>();
builder.Services.AddSingleton<DataAnalysisService>();
builder.Services.AddScoped<DatasetService>();

// Provedores de IA: o ativo é escolhido por "Ai:Provider" no appsettings.json.
builder.Services.AddHttpClient<GeminiProvider>(c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<OpenAICompatibleProvider>(c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddTransient<IAiProvider>(sp => sp.GetRequiredService<GeminiProvider>());
builder.Services.AddTransient<IAiProvider>(sp => sp.GetRequiredService<OpenAICompatibleProvider>());
builder.Services.AddSingleton<AnthropicProvider>();
builder.Services.AddTransient<IAiProvider>(sp => sp.GetRequiredService<AnthropicProvider>());
builder.Services.AddScoped<AiInsightsService>();

// Atrás do Nginx: respeita X-Forwarded-For / X-Forwarded-Proto (HTTPS, IP real do cliente).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (builder.Configuration.GetValue("UseHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAccountEndpoints();

app.Run();
