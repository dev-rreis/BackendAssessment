using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

class Program
{
    static async Task Main(string[] args)
    {
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        var serviceProvider = serviceCollection.BuildServiceProvider();
        var app = serviceProvider.GetService<App>();

        if (app != null)
        {
            await app.RunAsync(args);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Load configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        services.AddSingleton(configuration);
        services.Configure<GitHubSettings>(configuration.GetSection("GitHub"));

        services.AddHttpClient();
        services.AddLogging(configure => configure.AddConsole());

        services.AddTransient<App>();
        services.AddTransient<IGitHubService, GitHubService>();
    }
}

public class App
{
    private readonly IGitHubService _gitHubService;
    private readonly ILogger<App> _logger;

    public App(IGitHubService gitHubService, ILogger<App> logger)
    {
        _gitHubService = gitHubService;
        _logger = logger;
    }

    public async Task RunAsync(string[] args)
    {
        _logger.LogInformation("Fetching repository files...");
        var fileUrls = await _gitHubService.GetJsAndTsFiles();

        _logger.LogInformation($"Found {fileUrls.Count} JavaScript/TypeScript files.");
        var letterCounts = await _gitHubService.AnalyzeFiles(fileUrls);

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine("Letter Frequency (Descending):");
        foreach (var (letter, count) in letterCounts.OrderByDescending(kv => kv.Value))
        {
            stringBuilder.AppendLine($"{letter}: {count}");
        }
        _logger.LogInformation(stringBuilder.ToString());
    }
}

public interface IGitHubService
{
    Task<List<string>> GetJsAndTsFiles(string path = "");
    Task<Dictionary<char, int>> AnalyzeFiles(IEnumerable<string> fileUrls);
}

public class GitHubService : IGitHubService
{
    private readonly HttpClient _httpClient;
    private readonly GitHubSettings _settings;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(HttpClient httpClient, IOptions<GitHubSettings> settings, ILogger<GitHubService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Add("User-Agent", _settings.UserAgent);
    }

    public async Task<List<string>> GetJsAndTsFiles(string path = "")
    {
        var fileUrls = new List<string>();
        var url = $"{_settings.ApiBase}/{_settings.Owner}/{_settings.Repo}/contents/{path}";

        try
        {
            var response = await _httpClient.GetStringAsync(url);
            var items = JsonSerializer.Deserialize<List<GitHubItem>>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (items == null) return fileUrls;

            foreach (var item in items)
            {
                switch (item.Type)
                {
                    case "file" when _settings.TargetExtensions.Any(ext => item.Name.EndsWith(ext)):
                        if (item.DownloadUrl != null) fileUrls.Add(item.DownloadUrl);
                        break;
                    case "dir":
                        fileUrls.AddRange(await GetJsAndTsFiles(item.Path));
                        break;
                }
            }
        }
        catch (HttpRequestException e)
        {
            _logger.LogError($"Request error: {e.Message}");
        }
        catch (JsonException e)
        {
            _logger.LogError($"JSON error: {e.Message}");
        }

        return fileUrls;
    }

    public async Task<Dictionary<char, int>> AnalyzeFiles(IEnumerable<string> fileUrls)
    {
        var letterCounts = new Dictionary<char, int>();

        foreach (var url in fileUrls)
        {
            var content = await _httpClient.GetStringAsync(url);
            foreach (var letter in Regex.Replace(content, @"[^a-zA-Z]", "").ToLower().Where(char.IsLetter))
            {
                letterCounts.TryAdd(letter, 0);
                letterCounts[letter]++;
            }
        }

        return letterCounts;
    }
}

public class GitHubItem
{
    public string Name { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string Type { get; set; } = null!;

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }
}

public class GitHubSettings
{
    public string ApiBase { get; set; } = null!;
    public string Owner { get; set; } = null!;
    public string Repo { get; set; } = null!;
    public string[] TargetExtensions { get; set; } = null!;
    public string UserAgent { get; set; } = null!;
}
