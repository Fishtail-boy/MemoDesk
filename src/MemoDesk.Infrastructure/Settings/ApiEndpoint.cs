namespace MemoDesk.Infrastructure.Settings;

/// <summary>Remote credentials require TLS; plain HTTP is reserved for local compatible servers.</summary>
public static class ApiEndpoint
{
    public const string ValidationMessage = "接口地址须为 HTTPS（本机 localhost/回环地址可用 HTTP），且不能包含账号、查询参数或片段。";

    public static bool TryCreate(string baseUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) return false;
        endpoint = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        return true;
    }
}
