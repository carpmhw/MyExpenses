using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class IdempotencyRequestHasherTests
{
    /// <summary>Verifies canonical hashing ignores JSON property order and omitted null values.</summary>
    [Fact]
    public void Compute_UsesCanonicalPayloadRepresentation()
    {
        var first = IdempotencyRequestHasher.Compute(new { amount = 100, note = (string?)null, nested = new { b = 2, a = 1 } });
        var second = IdempotencyRequestHasher.Compute(new { nested = new { a = 1, b = 2 }, amount = 100 });

        Assert.Equal(first, second);
    }

    /// <summary>Verifies semantically different command payloads produce different hashes.</summary>
    [Fact]
    public void Compute_DifferentiatesPayloadValues()
    {
        var first = IdempotencyRequestHasher.Compute(new { amount = 100 });
        var second = IdempotencyRequestHasher.Compute(new { amount = 101 });

        Assert.NotEqual(first, second);
    }
}
