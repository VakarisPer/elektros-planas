using System.ClientModel;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.AI;
using OpenAI;
using vamos.Components;
using vamos.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<SubmissionWriter>();
builder.Services.AddScoped<ConsumptionAnalyzer>();
builder.Services.AddScoped<QuestionnaireState>();

// ── AI advisor (Microsoft Agent Framework over OpenRouter's OpenAI-compatible API) ──
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection("OpenRouter"));
builder.Services.Configure<TariffOptions>(builder.Configuration.GetSection("Tariffs"));
builder.Services.Configure<SolarOptions>(builder.Configuration.GetSection("Solar"));
builder.Services.AddSingleton<PlanCalculator>();
builder.Services.AddSingleton<SolarAdvisor>();
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
