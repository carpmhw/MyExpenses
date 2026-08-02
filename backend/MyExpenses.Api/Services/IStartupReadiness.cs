namespace MyExpenses.Api.Services;

/// <summary>提供 startup 初始化是否已完成的唯讀狀態給 readiness health check。</summary>
public interface IStartupReadiness
{
    /// <summary>取得 startup migration、seed 與初始化是否已成功完成。</summary>
    bool IsReady { get; }
}
