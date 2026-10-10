using System.ClientModel;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.AI;
using OpenAI;
using vamos.Components;
using vamos.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // The saved survey (hourly profile matrices + chat) is read back from localStorage over SignalR.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 512 * 1024);

builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<SubmissionWriter>();
builder.Services.AddScoped<ConsumptionAnalyzer>();
builder.Services.AddScoped<QuestionnaireState>();

// ── AI advisor (Microsoft Agent Framework over OpenRouter's OpenAI-compatible API) ──
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection("OpenRouter"));
builder.Services.Configure<TariffOptions>(builder.Configuration.GetSection("Tariffs"));
builder.Services.Configure<SolarOptions>(builder.Configuration.GetSection("Solar"));
builder.Services.AddSingleton(sp => new TariffCatalog(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
builder.Services.AddSingleton<BillEngine>();
builder.Services.AddSingleton<PlanCalculator>();
builder.Services.AddSingleton<SolarAdvisor>();

// ESO "Saulės elektrinės prijungimo galimybės" lookup (cookies are handled manually per request).
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<EsoConnectionService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ElektrosPlanas/1.0; Hack4Vilnius)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });
builder.Services.AddSingleton<AdvisorPrompt>();
builder.Services.AddScoped<ElectricityAdvisorAgent>();

var openRouter = builder.Configuration.GetSection("OpenRouter").Get<OpenRouterOptions>() ?? new();
if (openRouter.IsConfigured)
{
    builder.Services.AddSingleton<IChatClient>(_ =>
        new OpenAIClient(
                new ApiKeyCredential(openRouter.ApiKey!),
                new OpenAIClientOptions { Endpoint = new Uri(openRouter.BaseUrl) })
            .GetChatClient(openRouter.Model)
            .AsIChatClient());
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
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

app.Run();
