using Microsoft.AspNetCore.DataProtection;
using Fleet.Client.Core;
using FleetDashboard;
using FleetDashboard.Components;

var builder = WebApplication.CreateBuilder(args);

// Persist the data-protection key ring outside the container filesystem.
//
// Without this, ASP.NET generates a fresh key ring on every start. Any antiforgery cookie
// a browser is still holding was encrypted with the previous key, so the negotiate request
// that establishes the Blazor circuit is rejected — and the failure is silent from the
// user's side: the page renders its prerendered HTML and simply never becomes interactive.
//
// It also matters beyond restarts: more than one replica of this container would otherwise
// each hold a different key ring and reject each other's cookies.
var keyRing = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRing))
{
    Directory.CreateDirectory(keyRing);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
        .SetApplicationName("fleet-dashboard");
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Everything below is scoped, meaning one instance per Blazor circuit — per browser
// session, in practice.
//
// A singleton store shared by all viewers would be cheaper: one socket to the API feeding
// every tab. It is deliberately not done that way. Each of the other dashboards (WinUI,
// Qt, Electron) is a separate process holding its own connection, so a shared feed would
// give this client an advantage the others cannot have and make the comparison meaningless.
// Scoping per circuit makes Blazor pay the same transport cost as the rest.
builder.Services.AddScoped<FleetStore>();

builder.Services.AddScoped(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new FleetClientOptions
    {
        BaseUrl = config["Api:BaseUrl"] ?? "http://localhost:8080",
        // 4 Hz matches the API's default cadence. The comparison sweeps this value, which
        // is why it is configuration rather than a constant.
        MaxRateHz = config.GetValue("Api:MaxRateHz", 4.0),
    };
});

builder.Services.AddScoped(sp => new FleetConnection(
    sp.GetRequiredService<FleetStore>(),
    sp.GetRequiredService<FleetClientOptions>(),
    http: null,
    log: sp.GetRequiredService<ILogger<FleetConnection>>()));

builder.Services.AddScoped<FleetView>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
