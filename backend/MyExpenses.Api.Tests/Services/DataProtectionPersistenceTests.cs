using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class DataProtectionPersistenceTests
{
    /// <summary>驗證 application recreation 使用相同 key directory 與 discriminator 時仍可解密 session cookie。</summary>
    [Fact]
    public async Task RecreatedApplication_DecryptsSessionCookieFromRetainedKeyDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var options = new PersistentDataProtectionOptions
        {
            ApplicationName = "MyExpenses",
            KeyDirectory = temporaryDirectory.RootPath,
        };

        string cookieValue;
        using (var firstApplication = CreateDataProtectionProvider(options))
        {
            var firstProvider = firstApplication.GetRequiredService<IDataProtectionProvider>();
            var protector = firstProvider.CreateProtector("MyExpenses.Session");
            cookieValue = Convert.ToBase64String(
                protector.Protect(Encoding.UTF8.GetBytes("1:1234567890")));
        }

        using var recreatedApplication = CreateDataProtectionProvider(options);
        var recreatedProvider = recreatedApplication.GetRequiredService<IDataProtectionProvider>();
        var middleware = new SessionCookieMiddleware(
            ContinueRequestAsync,
            NullLogger<SessionCookieMiddleware>.Instance);
        var context = CreateAuthenticatedContext(cookieValue);

        await middleware.InvokeAsync(
            context,
            recreatedProvider);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>提供不改變 response 的 middleware continuation，讓 session cookie 驗證可獨立測試。</summary>
    private static Task ContinueRequestAsync(HttpContext _)
        => Task.CompletedTask;

    /// <summary>驗證 Production key directory 無法寫入時 startup validation 會失敗且不洩漏 key material。</summary>
    [Fact]
    public void StartupValidator_RejectsUnreadableKeyDirectoryWithoutLoggingKeyMaterial()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var keyPath = Path.Combine(temporaryDirectory.RootPath, "key-directory");
        const string keyMaterial = "private-key-material-must-not-appear";
        File.WriteAllText(keyPath, keyMaterial);

        var validator = new DataProtectionStartupValidator(
            new PersistentDataProtectionOptions
            {
                ApplicationName = "MyExpenses",
                KeyDirectory = keyPath,
            },
            isProduction: true);

        var error = Assert.Throws<InvalidOperationException>(() => validator.Validate());

        Assert.DoesNotContain(keyMaterial, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>建立以指定 Production options 設定的 Data Protection provider，模擬容器重建。</summary>
    private static ServiceProvider CreateDataProtectionProvider(PersistentDataProtectionOptions options)
    {
        var services = new ServiceCollection();
        DataProtectionRegistration.Add(services, options, isProduction: true);
        return services.BuildServiceProvider();
    }

    /// <summary>建立帶有 session claims 與 cookie 的 authenticated HTTP context。</summary>
    private static DefaultHttpContext CreateAuthenticatedContext(string cookieValue)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim("jwtExp", "1234567890"),
            ],
            "Bearer")),
        };
        context.Request.Headers.Cookie = $"mx_session={cookieValue}";
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>提供每個測試獨立且自動清理的暫存目錄。</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>建立測試暫存目錄。</summary>
        public TemporaryDirectory()
        {
            RootPath = Directory.CreateTempSubdirectory("myexpenses-data-protection-tests-").FullName;
        }

        /// <summary>取得測試暫存目錄的絕對路徑。</summary>
        public string RootPath { get; }

        /// <summary>刪除測試產生的所有資料。</summary>
        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
