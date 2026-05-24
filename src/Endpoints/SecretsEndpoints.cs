using Thio_Universal_Agent.Handlers;

namespace Thio_Universal_Agent.Endpoints;

/// <summary>
/// Exposes encrypted secret storage to the web UI via minimal API endpoints.
/// <list type="bullet">
///   <item><description><c>POST   /api/secrets/save</c>           — encrypt and persist a secret. Falls back to the server-session hash if <c>passwordHash</c> is omitted.</description></item>
///   <item><description><c>POST   /api/secrets/load</c>           — decrypt and return a secret. Falls back to the server-session hash if <c>passwordHash</c> is omitted. Stores the hash in the session on success.</description></item>
///   <item><description><c>GET    /api/secrets/{key}/exists</c>   — check whether a secret exists without decrypting it.</description></item>
///   <item><description><c>DELETE /api/secrets/{key}</c>          — permanently delete a stored secret.</description></item>
///   <item><description><c>GET    /api/secrets/vault/status</c>   — returns whether the vault is currently unlocked server-side.</description></item>
///   <item><description><c>POST   /api/secrets/vault/lock</c>     — clears the server-session hash, locking the vault.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Passwords are <b>never</b> sent to these endpoints. The browser hashes the user's password
/// (SHA-256) before sending. That hash is used by <see cref="ISecretProvider"/> to derive the
/// AES key via PBKDF2-SHA256.
/// </para>
/// <para>
/// Once a successful decrypt occurs the hash is stored in <see cref="VaultSession"/> for the
/// lifetime of the server process. Subsequent requests from the browser may omit
/// <c>passwordHash</c> and the session hash will be used transparently, so the vault stays
/// effectively unlocked across page navigations without requiring re-entry of the password.
/// </para>
/// </remarks>
internal static class SecretsEndpoints
{
    internal static void MapSecretsEndpoints(this WebApplication app)
    {
        // ── Save ──────────────────────────────────────────────────────────────

        app.MapPost("/api/secrets/save", (SaveSecretRequest req, ISecretProvider secrets, VaultSession vaultSession) =>
        {
            if (string.IsNullOrWhiteSpace(req.KeyName))
                return Results.BadRequest("keyName is required.");
            if (string.IsNullOrWhiteSpace(req.Secret))
                return Results.BadRequest("secret is required.");

            string? hash = string.IsNullOrWhiteSpace(req.PasswordHash) ? vaultSession.PasswordHash : req.PasswordHash;
            if (string.IsNullOrWhiteSpace(hash))
                return Results.BadRequest("passwordHash is required and the vault is not unlocked.");

            secrets.SaveSecret(req.KeyName, req.Secret, hash);

            // Keep session hash current in case a new password was explicitly provided.
            if (!string.IsNullOrWhiteSpace(req.PasswordHash))
                vaultSession.SetHash(req.PasswordHash);

            return Results.Ok();
        });

        // ── Load ──────────────────────────────────────────────────────────────

        app.MapPost("/api/secrets/load", (LoadSecretRequest req, ISecretProvider secrets, VaultSession vaultSession) =>
        {
            if (string.IsNullOrWhiteSpace(req.KeyName))
                return Results.BadRequest("keyName is required.");

            string? hash = string.IsNullOrWhiteSpace(req.PasswordHash) ? vaultSession.PasswordHash : req.PasswordHash;
            if (string.IsNullOrWhiteSpace(hash))
                return Results.BadRequest("passwordHash is required and the vault is not unlocked.");

            string? value = secrets.LoadSecret(req.KeyName, hash);

            // Return 401 when the file exists but decryption failed (wrong password),
            // and 404 when no secret has been saved for that key yet.
            if (value is null)
            {
                bool exists = secrets.SecretExists(req.KeyName);
                return exists ? Results.Unauthorized() : Results.NotFound();
            }

            // Persist the hash in the session on first successful decrypt.
            if (!string.IsNullOrWhiteSpace(req.PasswordHash))
                vaultSession.SetHash(req.PasswordHash);

            return Results.Ok(new { secret = value });
        });

        // ── Exists ────────────────────────────────────────────────────────────

        app.MapGet("/api/secrets/{key}/exists", (string key, ISecretProvider secrets) =>
        {
            if (string.IsNullOrWhiteSpace(key))
                return Results.BadRequest("key is required.");

            return Results.Ok(new { exists = secrets.SecretExists(key) });
        });

        // ── Delete ────────────────────────────────────────────────────────────

        app.MapDelete("/api/secrets/{key}", (string key, ISecretProvider secrets) =>
        {
            if (string.IsNullOrWhiteSpace(key))
                return Results.BadRequest("key is required.");

            secrets.DeleteSecret(key);
            return Results.NoContent();
        });

        // ── Vault status ──────────────────────────────────────────────────────

        app.MapGet("/api/secrets/vault/status", (VaultSession vaultSession) =>
            Results.Ok(new { unlocked = vaultSession.IsUnlocked }));

        // ── Vault lock ────────────────────────────────────────────────────────

        app.MapPost("/api/secrets/vault/lock", (VaultSession vaultSession) =>
        {
            vaultSession.Clear();
            return Results.Ok();
        });
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

internal sealed record SaveSecretRequest(string KeyName, string Secret, string? PasswordHash);
internal sealed record LoadSecretRequest(string KeyName, string? PasswordHash);
