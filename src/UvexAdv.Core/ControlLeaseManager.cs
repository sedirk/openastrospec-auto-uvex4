using System.Security.Cryptography;

namespace UvexAdv.Core;

public sealed record ControlLease(string Token, string Owner, DateTimeOffset ExpiresUtc);

public sealed class ControlLeaseManager(TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private ControlLease? active;

    public ControlLease? Current
    {
        get
        {
            lock (gate)
            {
                ExpireIfNeeded();
                return active;
            }
        }
    }

    public ControlLease Acquire(string owner, TimeSpan requestedTtl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var ttl = ClampTtl(requestedTtl);
        lock (gate)
        {
            ExpireIfNeeded();
            if (active is not null)
            {
                throw new InvalidOperationException($"UVEX control is leased by '{active.Owner}' until {active.ExpiresUtc:O}.");
            }

            active = new ControlLease(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                owner.Trim(),
                clock.GetUtcNow().Add(ttl));
            return active;
        }
    }

    public ControlLease Renew(string token, TimeSpan requestedTtl)
    {
        lock (gate)
        {
            ExpireIfNeeded();
            RequireToken(token);
            active = active! with { ExpiresUtc = clock.GetUtcNow().Add(ClampTtl(requestedTtl)) };
            return active;
        }
    }

    public void Release(string token)
    {
        lock (gate)
        {
            ExpireIfNeeded();
            RequireToken(token);
            active = null;
        }
    }

    public void Require(string? token)
    {
        lock (gate)
        {
            ExpireIfNeeded();
            RequireToken(token);
        }
    }

    private static TimeSpan ClampTtl(TimeSpan ttl) =>
        TimeSpan.FromSeconds(Math.Clamp(ttl.TotalSeconds, 5, 120));

    private void ExpireIfNeeded()
    {
        if (active is not null && active.ExpiresUtc <= clock.GetUtcNow())
        {
            active = null;
        }
    }

    private void RequireToken(string? token)
    {
        if (active is null || string.IsNullOrWhiteSpace(token) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(active.Token),
                System.Text.Encoding.UTF8.GetBytes(token)))
        {
            throw new UnauthorizedAccessException("A valid, unexpired UVEX control lease is required.");
        }
    }
}
