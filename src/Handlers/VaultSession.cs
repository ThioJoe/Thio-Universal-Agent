namespace Thio_Universal_Agent.Handlers;

/// <summary>
/// Singleton that holds the vault password hash in memory for the lifetime of the server process.
/// This allows the vault to remain effectively "unlocked" across browser navigations without
/// requiring the user to re-enter their vault password each time the Config page is loaded.
/// </summary>
/// <remarks>
/// The hash stored here is the same client-side SHA-256 hash that the browser sends to
/// <c>/api/secrets/load</c> and <c>/api/secrets/save</c>. It is written the first time any
/// secret is successfully decrypted, and cleared only when the user explicitly locks the vault
/// via <c>POST /api/secrets/vault/lock</c> or when the server process restarts.
/// </remarks>
public sealed class VaultSession
{
    private volatile string? _passwordHash;

    /// <summary>Gets whether the vault has been unlocked at least once this server session.</summary>
    public bool IsUnlocked => _passwordHash is not null;

    /// <summary>Gets the stored password hash, or <c>null</c> if the vault has never been unlocked this session.</summary>
    public string? PasswordHash => _passwordHash;

    /// <summary>Stores the vault password hash, marking the session as unlocked.</summary>
    public void SetHash(string hash) => _passwordHash = hash;

    /// <summary>Clears the stored hash, locking the vault for the remainder of this server session.</summary>
    public void Clear() => _passwordHash = null;
}
