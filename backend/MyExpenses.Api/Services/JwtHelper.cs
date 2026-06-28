using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class JwtHelper
{
    public static (string token, long exp) GenerateToken(User user, string secret, string? issuer, string? audience, int expiryMinutes = 1440)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new("displayName", user.DisplayName),
            new("isTwoFactorEnabled", user.IsTwoFactorEnabled.ToString().ToLower()),
            new("tokenVersion", user.TokenVersion.ToString()),
        };

        var expires = DateTime.UtcNow.AddMinutes(expiryMinutes);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expires,
            signingCredentials: credentials
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), ((DateTimeOffset)expires).ToUnixTimeSeconds());
    }

    public static string GenerateTempToken(int userId, string secret, string? issuer, string? audience)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("tokenType", "temp")
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static int? ValidateTempToken(string tempToken, string secret, string? issuer, string? audience)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var handler = new JwtSecurityTokenHandler();
            var result = handler.ValidateToken(tempToken, new TokenValidationParameters
            {
                ValidateIssuer = issuer != null,
                ValidIssuer = issuer,
                ValidateAudience = audience != null,
                ValidAudience = audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
            }, out var validatedToken);

            var tokenType = result.FindFirst("tokenType")?.Value;
            if (tokenType != "temp") return null;

            var userIdStr = result.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out var userId) ? userId : null;
        }
        catch
        {
            return null;
        }
    }
}
