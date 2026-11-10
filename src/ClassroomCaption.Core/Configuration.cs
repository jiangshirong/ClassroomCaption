using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassroomCaption.Core;

public sealed class OverlaySettings
{
    public double Left { get; set; } = 160;
    public double Top { get; set; } = 640;
    public double Width { get; set; } = 1050;
    public double Height { get; set; } = 245;
    public double ChineseSize { get; set; } = 40;
    public double RussianSize { get; set; } = 22;
    public double PanelOpacity { get; set; } = .92;
    public string ChineseColor { get; set; } = "#FFFFFF";
    public string RussianColor { get; set; } = "#FFD98A";
    public bool Outline { get; set; } = true;
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public bool AdaptiveContrast { get; set; }
    public bool LightTextBackground { get; set; }
    public double TextBackgroundOpacity { get; set; } = .75;
    public void UpgradeAppearance()
    {
        Locked = false; ClickThrough = false;
        if (string.Equals(RussianColor, "#D6E9E3", StringComparison.OrdinalIgnoreCase)) RussianColor = "#FFD98A";
        if (Math.Abs(PanelOpacity - .78) < .0001) PanelOpacity = .92;
    }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int DataLocationVersion { get; set; }
    public string Region { get; set; } = "cn-beijing";
    public string WorkspaceId { get; set; } = "";
    public string LiveTranslateModel => LiveTranslateProtocol.Model;
    public string InputDeviceId { get; set; } = "";
    public bool NoiseReductionEnabled { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string Glossary { get; set; } = ""; // Course terminology is embedded and loaded lazily.
    public OverlaySettings Overlay { get; set; } = new();

    public Uri GetRealtimeUri()
    {
        if (Region is not ("cn-beijing" or "ap-southeast-1")) throw new UserFacingException("请选择北京或新加坡地域。");
        if (string.IsNullOrWhiteSpace(WorkspaceId) || WorkspaceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new UserFacingException("请填写有效的 LiveTranslate Workspace ID。");
        return EndpointRules.Realtime($"wss://{WorkspaceId}.{Region}.maas.aliyuncs.com/api-ws/v1/realtime");
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (Glossary.Length > 12000) throw new UserFacingException("术语表请控制在 12000 个字符以内。");
        _ = LiveTranslateProtocol.Terms(Glossary);

    }
}

public static class EndpointRules
{
    public static Uri Realtime(string value) => Check(value, "wss", h => h.EndsWith(".maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase));
    private static Uri Check(string value, string scheme, Func<string, bool> host)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != scheme || !host(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UserFacingException($"接口必须使用厂商官方域名与 {scheme.ToUpperInvariant()}，不能含账号、查询参数或自定义端口。");
        return uri;
    }
}

public sealed class UserFacingException(string message) : Exception(message);

public static class SafeErrors
{
    public static string Describe(Exception error) => error switch
    {
        UserFacingException e => e.Message,
        OperationCanceledException => "操作已取消或超时，请检查网络后重试。",
        System.Security.Cryptography.CryptographicException => "解锁失败：密码不正确，或密钥文件已被修改。",
        UnauthorizedAccessException => "目录无法写入。请在设置中选择有写入权限的保存目录。",
        IOException => "文件读写失败，请检查保存目录、磁盘空间和设备连接。",
        System.Net.WebSockets.WebSocketException => "语音服务连接中断，请检查网络、地域和密钥配置。",
        HttpRequestException => "无法连接模型服务，请检查网络与接口设置。",
        JsonException => "模型返回格式无效或配置文件损坏，请重试或检查设置。",
        _ => "操作未完成，请检查设备与配置后重试。"
    };
    public static string HttpStatus(int status) => status switch
    {
        401 or 403 => "鉴权失败，请检查对应厂商的 API 密钥、权限与地域。",
        402 => "模型账户余额或额度不足。",
        404 => "接口或模型不存在，请在设置中检查接口地址与模型名称。",
        429 => "调用受到限流，稍后会重试；请检查额度和请求频率。",
        >= 500 => "厂商服务暂时不可用，请稍后重试。",
        >= 300 and < 400 => "服务返回了重定向。为保护密钥，未跟随跳转，请核对官方接口地址。",
        _ => "模型拒绝了请求，请检查模型名称、权限与参数。"
    };
}

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class SettingsStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string VaultPath => Path.Combine(Root, "credentials.vault");
    public string CredentialsPath => Path.Combine(Root, "credentials.json");
    public ApiCredentials? LoadCredentials() => File.Exists(CredentialsPath)
        ? JsonSerializer.Deserialize<ApiCredentials>(File.ReadAllText(CredentialsPath)) : null;
    public void SaveCredentials(ApiCredentials credentials) => AtomicFile.Write(CredentialsPath,
        JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true }));
    public AppSettings Load()
    {
        var settings = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new() : new();
        settings.Overlay.UpgradeAppearance();
        return settings;
    }
    public void Save(AppSettings settings) => AtomicFile.Write(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    public static void CheckWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $".write-check-{Guid.NewGuid():N}");
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.WriteByte(0); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
