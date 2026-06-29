using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SeedForge.Components;
using SeedForge.Components.Account;
using Microsoft.Extensions.Options;
using SeedForge.Data;
using SeedForge.Features;
using SeedForge.Features.Config;
using SeedForge.Services.Ai;
using SeedForge.Services.Apify;
using SeedForge.Services.Queues;
using SeedForge.Services.YouTube;
using SeedForge.Workers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// AI plumbing: per-slot options, resolver, LLM client + call-logging decorator.
builder.Services.AddAiServices(builder.Configuration);

// Apify ingestion boundary: typed client + ingestion service.
builder.Services.AddApifyServices(builder.Configuration);

// YouTube Data API boundary (Phase 6): typed client for channel resolution + recent-uploads listing.
builder.Services.AddYouTube(builder.Configuration);

// Pipeline slices, options, and the orchestrator.
builder.Services.AddFeatures(builder.Configuration);

// Durable, DB-backed queues over Video / ConceptJob rows.
builder.Services.AddQueues();

// Background workers: options + the shared pause/wake control.
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Workers"));
builder.Services.AddSingleton<WorkerControl>();

// Processing worker: drains the video queue (ingest → score → enqueue) one item per tick.
builder.Services.AddScoped<ProcessingIteration>();
builder.Services.AddHostedService<ProcessingWorker>();

// Concept worker: drains the concept queue (build one concept per job) on its own cadence.
builder.Services.AddScoped<ConceptIteration>();
builder.Services.AddHostedService<ConceptWorker>();

// Discovery worker (Phase 6): polls the channel library daily, enqueuing new uploads.
builder.Services.AddScoped<DiscoveryIteration>();
builder.Services.AddHostedService<DiscoveryWorker>();

var app = builder.Build();

// Create/upgrade the SQLite schema on startup so deployments self-provision (no manual migrate step).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SeedForge.Data.ApplicationDbContext>();
    db.Database.Migrate();

    // Seed the canonical config profiles on first run (idempotent) from the bound AI options.
    var aiOptions = scope.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
    await ProfileSeeder.SeedAsync(db, aiOptions);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
