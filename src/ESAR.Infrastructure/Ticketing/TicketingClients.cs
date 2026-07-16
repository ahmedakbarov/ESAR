using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Esar.Infrastructure.Ticketing;

public class ServiceNowOptions
{
    public bool Enabled { get; set; }
    public string InstanceUrl { get; set; } = string.Empty; // https://<instance>.service-now.com
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Table { get; set; } = "incident";
}

public class ServiceNowTicketingClient : ITicketingClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ServiceNowOptions _options;
    private readonly ILogger<ServiceNowTicketingClient> _logger;

    public ServiceNowTicketingClient(IHttpClientFactory httpFactory, IOptions<ServiceNowOptions> options,
        ILogger<ServiceNowTicketingClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string SystemName => "ServiceNow";

    public async Task<string?> CreateTicketAsync(Incident incident, CancellationToken ct = default)
    {
        if (!_options.Enabled) return null;
        var client = _httpFactory.CreateClient("ticketing");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.InstanceUrl.TrimEnd('/')}/api/now/table/{_options.Table}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                short_description = $"[ESAR] {incident.Title}",
                description = incident.Description,
                urgency = incident.Severity >= Domain.Enums.IncidentSeverity.High ? "1" : "2",
                category = "Security",
                correlation_id = incident.DedupKey
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var sysId = doc.RootElement.GetProperty("result").GetProperty("sys_id").GetString();
        _logger.LogInformation("ServiceNow ticket {SysId} created for incident {Incident}", sysId, incident.Id);
        return sysId;
    }
}

public class JiraOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty; // https://<org>.atlassian.net
    public string Email { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = "SEC";
    public string IssueType { get; set; } = "Task";
}

public class JiraTicketingClient : ITicketingClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly JiraOptions _options;
    private readonly ILogger<JiraTicketingClient> _logger;

    public JiraTicketingClient(IHttpClientFactory httpFactory, IOptions<JiraOptions> options,
        ILogger<JiraTicketingClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string SystemName => "Jira";

    public async Task<string?> CreateTicketAsync(Incident incident, CancellationToken ct = default)
    {
        if (!_options.Enabled) return null;
        var client = _httpFactory.CreateClient("ticketing");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Email}:{_options.ApiToken}"));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/rest/api/3/issue")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                fields = new
                {
                    project = new { key = _options.ProjectKey },
                    issuetype = new { name = _options.IssueType },
                    summary = $"[ESAR] {incident.Title}",
                    description = new
                    {
                        type = "doc",
                        version = 1,
                        content = new object[]
                        {
                            new
                            {
                                type = "paragraph",
                                content = new object[] { new { type = "text", text = incident.Description } }
                            }
                        }
                    },
                    labels = new[] { "esar", incident.Type.ToString().ToLowerInvariant() }
                }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var key = doc.RootElement.GetProperty("key").GetString();
        _logger.LogInformation("Jira issue {Key} created for incident {Incident}", key, incident.Id);
        return key;
    }
}
