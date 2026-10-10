using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using vamos.Models;

namespace vamos.Services;

/// <summary>
/// Queries ESO's public "Saulės elektrinės prijungimo galimybės" tool
/// (https://www.eso.lt/namams/gaminantis-vartotojas/saules-elektrines-prijungimo-galimybes/362).
/// The page posts <c>{"nr": "12345678"}</c> to <c>…/362/nrdata</c> with an XSRF cookie/header pair,
/// so we first load the page to obtain the cookies. Results are cached for a few hours.
/// </summary>
public sealed partial class EsoConnectionService
{
    public const string PageUrl = "https://www.eso.lt/namams/gaminantis-vartotojas/saules-elektrines-prijungimo-galimybes/362";
    private const string DataUrl = PageUrl + "/nrdata";
    private const string XsrfCookie = "SITEXSRF141";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EsoConnectionService> _logger;

    public EsoConnectionService(HttpClient http, IMemoryCache cache, ILogger<EsoConnectionService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public static bool IsValidObjectNumber(string? nr) => nr is not null && ObjectNumberRegex().IsMatch(nr.Trim());

    public async Task<EsoConnectionInfo> CheckAsync(string objectNumber, CancellationToken ct = default)
    {
        var nr = objectNumber.Trim();
        if (!IsValidObjectNumber(nr))
        {
            return new EsoConnectionInfo
            {
                ObjectNumber = nr,
                Error = "Objekto numeris turi būti 8 skaitmenys (rasite ESO savitarnoje, skiltyje „Objektai“).",
                CheckedAt = DateTime.Now
            };
        }

        if (_cache.TryGetValue<EsoConnectionInfo>(nr, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var result = await QueryAsync(nr, ct);
            if (result.Success)
            {
                _cache.Set(nr, result, TimeSpan.FromHours(6));
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "ESO connection check failed for object {Object}", nr);
            return new EsoConnectionInfo
            {
                ObjectNumber = nr,
                Error = "Nepavyko susisiekti su ESO. Bandykite vėliau arba patikrinkite ESO svetainėje.",
                CheckedAt = DateTime.Now
            };
        }
    }

    private async Task<EsoConnectionInfo> QueryAsync(string nr, CancellationToken ct)
    {
        // 1) Load the page to receive the session and XSRF cookies.
        using var page = await _http.GetAsync(PageUrl, ct);
        page.EnsureSuccessStatusCode();

        var cookies = page.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';', 2)[0]).Where(c => c.Contains('=')).ToList()
            : new List<string>();
        var xsrf = cookies.Select(c => c.Split('=', 2)).FirstOrDefault(kv => kv[0] == XsrfCookie)?[1];

        // 2) Post the object number exactly like the page does.
        using var request = new HttpRequestMessage(HttpMethod.Post, DataUrl)
        {
            // nr is validated to 8 digits, so it is safe to inline.
            Content = new StringContent($"{{\"nr\":\"{nr}\"}}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Referrer = new Uri(PageUrl);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.eso.lt");
        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        if (xsrf is not null)
        {
            request.Headers.TryAddWithoutValidation("X-" + XsrfCookie, WebUtility.UrlDecode(xsrf));
        }

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;

        var info = new EsoConnectionInfo { ObjectNumber = nr, CheckedAt = DateTime.Now };
        var success = root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        if (!success || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            info.Error = string.IsNullOrWhiteSpace(message) ? "ESO negrąžino duomenų šiam objektui." : message;
            return info;
        }

        info.Success = true;
        foreach (var row in data.EnumerateArray())
        {
            var galia = Text(row, "galia");
            var kw = double.TryParse(galia, NumberStyles.Float, Invariant, out var v) ? v : (double?)null;
            switch (Text(row, "tipas"))
            {
                case "LOG": info.AllowedConsumptionKw = kw; break;
                case "IGG": info.InstalledGenerationKw = kw; break;
                case "LGG": info.AllowedGenerationKw = kw; break;
            }

            if (string.IsNullOrWhiteSpace(info.Message) && Text(row, "text_col") is { Length: > 0 } text)
            {
                info.Message = text.Trim();
            }
        }

        if (info.Message is not null && ExportLimitRegex().Match(info.Message) is { Success: true } m &&
            double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, Invariant, out var limit))
        {
            info.ExportLimitKw = limit;
        }

        return info;
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex ObjectNumberRegex();

    [GeneratedRegex(@"ribojamas\s+iki\s+(\d+(?:[.,]\d+)?)\s*kW", RegexOptions.IgnoreCase)]
    private static partial Regex ExportLimitRegex();
}
