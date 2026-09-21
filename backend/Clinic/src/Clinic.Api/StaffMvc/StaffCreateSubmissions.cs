using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Clinic.Api.StaffMvc;

// Bounded, single-use UI form guard. Restart, eviction or another host fails closed;
// this is not durable operation idempotency or a substitute for application validation.
public sealed class StaffCreateSubmissions : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly object sync = new();
    public string Issue(string actor, Guid patient)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (sync) cache.Set(token, (actor, patient), new MemoryCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20), Size = 1 });
        return token;
    }
    public bool Consume(string? token, string actor, Guid patient)
    {
        if (token is not { Length: 64 }) return false;
        lock (sync)
        {
            if (!cache.TryGetValue<(string, Guid)>(token, out var owner) || owner != (actor, patient)) return false;
            cache.Remove(token);
            return true;
        }
    }
    public void Dispose() => cache.Dispose();
}
