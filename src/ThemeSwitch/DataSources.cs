using System.Net.Http.Headers;

namespace ThemeSwitch;

/// <summary>Fetches raw JSON. Swappable so end-to-end tests can run the real exe without the network.</summary>
interface IDataSource
{
    Task<string> GetAsync(string url, CancellationToken ct);
}

sealed class HttpDataSource : IDataSource, IDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public HttpDataSource()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ThemeSwitch", AppInfo.Version));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/apictapx/themeswitch)"));
    }

    public Task<string> GetAsync(string url, CancellationToken ct) => _http.GetStringAsync(url, ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Serves canned responses from a folder: ipwhois.json, ipapi.json, forecast.json. A missing file
/// behaves like a network failure, which is how tests simulate outages.
/// </summary>
sealed class FixtureDataSource(string directory) : IDataSource
{
    public Task<string> GetAsync(string url, CancellationToken ct)
    {
        string host = new Uri(url).Host;
        string file = host switch
        {
            "ipwho.is" => "ipwhois.json",
            "ipapi.co" => "ipapi.json",
            "api.open-meteo.com" => "forecast.json",
            _ => throw new HttpRequestException($"no fixture for {host}"),
        };
        string path = Path.Combine(directory, file);
        if (!File.Exists(path)) throw new HttpRequestException($"fixture {file} missing (simulated outage)");
        return File.ReadAllTextAsync(path, ct);
    }
}
