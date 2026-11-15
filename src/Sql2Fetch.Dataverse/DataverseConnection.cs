using System.Net.Http.Headers;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Sql2Fetch.Dataverse;

/// <summary>登录方式,对应 XrmToolBox 连接向导里的用户认证选项。</summary>
public enum AuthMode
{
    /// <summary>OAuth 交互式登录:打开系统浏览器,支持 MFA。</summary>
    Interactive = 0,
    /// <summary>用户名 + 密码(ROPC)。账号开了 MFA 或是联合身份时不可用。</summary>
    UsernamePassword = 1,
    /// <summary>设备码:在任意设备的浏览器里输入代码。</summary>
    DeviceCode = 2,
}

/// <summary>
/// 用户登录(浏览器交互 / 用户名密码 / 设备码)+ 带 Bearer 的 HttpClient。
/// 令牌缓存持久化到用户目录,下次启动不用重新登录。
/// </summary>
public sealed class DataverseConnection : IDisposable
{
    /// <summary>Microsoft 公开的 Power Platform 客户端 ID(pac CLI / XrmToolBox 都用它),支持设备码流程。</summary>
    public const string DefaultClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    private readonly IPublicClientApplication _app;
    private readonly string[] _scopes;
    private readonly HttpClient _http;
    private IAccount? _account;

    private DataverseConnection(Uri url, IPublicClientApplication app)
    {
        Url = url;
        _app = app;
        _scopes = new[] { new Uri(url, "/.default").ToString() };
        _http = new HttpClient { BaseAddress = new Uri(url, "/api/data/v9.2/"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        _http.DefaultRequestHeaders.Add("OData-Version", "4.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri Url { get; }
    public string? UserName => _account?.Username;
    public bool IsConnected => _account is not null;

    public static async Task<DataverseConnection> CreateAsync(string environmentUrl, string? clientId = null)
    {
        var url = new Uri(environmentUrl.TrimEnd('/') + "/");
        var app = PublicClientApplicationBuilder
            .Create(clientId ?? DefaultClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
            .WithRedirectUri("http://localhost")
            .Build();

        // 持久化令牌缓存(macOS 用 Keychain,Windows 用 DPAPI,Linux 用 libsecret)
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sql2fetch");
        Directory.CreateDirectory(cacheDir);
        var storage = new StorageCreationPropertiesBuilder("msal_cache.dat", cacheDir)
            .WithMacKeyChain("Sql2Fetch", "MSALCache")
            .WithLinuxKeyring("com.sql2fetch.tokencache", MsalCacheHelper.LinuxKeyRingDefaultCollection, "Sql2Fetch token cache",
                new KeyValuePair<string, string>("Version", "1"), new KeyValuePair<string, string>("Product", "Sql2Fetch"))
            .Build();
        var helper = await MsalCacheHelper.CreateAsync(storage);
        helper.RegisterCache(app.UserTokenCache);

        return new DataverseConnection(url, app);
    }

    /// <summary>
    /// 先尝试静默登录(缓存里有匹配的账号);不行再按 <paramref name="mode"/> 走对应的登录流程。
    /// <paramref name="userName"/> 可选:交互式登录时预填到登录页,并用来在缓存里挑账号;用户名密码方式下必填。
    /// </summary>
    public async Task ConnectAsync(AuthMode mode, string? userName, string? password,
        Func<string, string, Task> deviceCodePrompt, CancellationToken ct = default)
    {
        userName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();

        if (mode == AuthMode.UsernamePassword && (userName is null || string.IsNullOrEmpty(password)))
            throw new ArgumentException("请输入用户名和密码");

        var accounts = await _app.GetAccountsAsync();
        var cached = userName is null
            ? accounts.FirstOrDefault()
            : accounts.FirstOrDefault(a => string.Equals(a.Username, userName, StringComparison.OrdinalIgnoreCase));

        if (cached is not null)
        {
            try
            {
                var silent = await _app.AcquireTokenSilent(_scopes, cached).ExecuteAsync(ct);
                _account = silent.Account;
                return;
            }
            catch (MsalUiRequiredException)
            {
                // 缓存过期,走下面的登录流程
            }
        }

        AuthenticationResult result;
        switch (mode)
        {
            case AuthMode.Interactive:
                var interactive = _app.AcquireTokenInteractive(_scopes)
                    .WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(new SystemWebViewOptions
                    {
                        HtmlMessageSuccess = "<html><head><meta charset='utf-8'><title>Sql2Fetch</title></head><body style='font-family:sans-serif'><h3>登录成功</h3><p>可以关闭此页面,回到 Sql2Fetch。</p></body></html>",
                        HtmlMessageError = "<html><head><meta charset='utf-8'><title>Sql2Fetch</title></head><body style='font-family:sans-serif'><h3>登录失败</h3><p>{0}: {1}</p></body></html>",
                    });
                // 指定了用户名就预填并直接进入该账号;否则让用户选账号
                interactive = userName is not null
                    ? interactive.WithLoginHint(userName)
                    : interactive.WithPrompt(Prompt.SelectAccount);
                result = await interactive.ExecuteAsync(ct);
                break;

            case AuthMode.UsernamePassword:
#pragma warning disable CS0618 // ROPC 已被 MSAL 标记为过时,但这是 XrmToolBox 式"用户名+密码"登录的唯一途径
                result = await _app.AcquireTokenByUsernamePassword(_scopes, userName, password).ExecuteAsync(ct);
#pragma warning restore CS0618
                break;

            default:
                result = await _app.AcquireTokenWithDeviceCode(_scopes, code => deviceCodePrompt(code.VerificationUrl, code.UserCode))
                    .ExecuteAsync(ct);
                break;
        }

        _account = result.Account;
    }

    public async Task SignOutAsync()
    {
        foreach (var account in await _app.GetAccountsAsync())
            await _app.RemoveAsync(account);
        _account = null;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_account is null) throw new InvalidOperationException("尚未登录");
        var result = await _app.AcquireTokenSilent(_scopes, _account).ExecuteAsync(ct);
        return result.AccessToken;
    }

    /// <summary>发 GET 请求,返回 JSON 字符串。相对路径基于 /api/data/v9.2/。</summary>
    public async Task<string> GetAsync(string relativeUrl, CancellationToken ct = default, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new DataverseException((int)response.StatusCode, body);

        return body;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class DataverseException : Exception
{
    public int StatusCode { get; }
    public string Body { get; }

    public DataverseException(int statusCode, string body) : base(ExtractMessage(body) ?? $"HTTP {statusCode}")
    {
        StatusCode = statusCode;
        Body = body;
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return null;
    }
}
