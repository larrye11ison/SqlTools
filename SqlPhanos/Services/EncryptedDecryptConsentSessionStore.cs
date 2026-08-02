using System;
using System.Collections.Concurrent;

namespace SqlPhanos.Services;

/// <summary>
/// Remembers, per connection profile, whether the user has already answered the "decrypt
/// encrypted modules via DAC to build the dependency index" prompt this app session - so
/// re-refreshing the same connection's index, or opening a second Dependency Explorer tab
/// against it, doesn't ask again. Deliberately in-memory only and never written to disk:
/// decrypting protected code is a sensitive, explicit action, so the answer resets the next
/// time SqlPhanos is launched rather than being a silent, persisted bypass.
/// </summary>
public static class EncryptedDecryptConsentSessionStore
{
    private static readonly ConcurrentDictionary<Guid, bool> _decisions = new();

    /// <summary>True/false if already answered this session for this connection, else null.</summary>
    public static bool? TryGet(Guid connectionProfileId)
        => _decisions.TryGetValue(connectionProfileId, out var allowed) ? allowed : null;

    public static void Remember(Guid connectionProfileId, bool allowed)
        => _decisions[connectionProfileId] = allowed;
}
