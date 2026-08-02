namespace MyExpenses.Api.Options;

/// <summary>定義 MyExpenses 對外暴露範圍與 reverse proxy security boundary。</summary>
public sealed class DeploymentOptions
{
    /// <summary>取得 deployment 設定區段名稱。</summary>
    public const string SectionName = "Deployment";

    /// <summary>取得部署模式，預設只允許 localhost 存取。</summary>
    public DeploymentMode Mode { get; set; } = DeploymentMode.Local;

    /// <summary>取得應用程式 listener 的明確 bind address。</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>取得使用者瀏覽器看見的 public origin。</summary>
    public string? PublicOrigin { get; set; }

    /// <summary>取得是否啟用 session cookie 的 Secure attribute。</summary>
    public bool SecureCookies { get; set; }

    /// <summary>取得可提供 forwarded headers 的明確 proxy IP allowlist。</summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>取得可提供 forwarded headers 的明確 proxy network allowlist。</summary>
    public List<string> TrustedNetworks { get; set; } = [];
}

/// <summary>列舉受支援的部署暴露模式。</summary>
public enum DeploymentMode
{
    /// <summary>只允許本機 loopback 存取，使用 HTTP。</summary>
    Local,

    /// <summary>明確暴露到受信任的 home network。</summary>
    Lan,

    /// <summary>經由 HTTPS reverse proxy 提供 remote access。</summary>
    Remote,
}
