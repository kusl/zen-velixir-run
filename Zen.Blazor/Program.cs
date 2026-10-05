using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Zen.Blazor.Components;
using Zen.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port))
{
    builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
}

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

builder.Services.AddSingleton<MessageBoard>();
builder.Services.AddSingleton<ClockService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClockService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o =>
    {
        o.DisconnectedCircuitMaxRetained = 20;
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(1);
        o.MaxBufferedUnacknowledgedRenderBatches = 2;
    });

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
