using Thio_Universal_Agent.Handlers;

namespace Thio_Universal_Agent.Endpoints;

/// <summary>
/// Exposes encrypted secret storage to the web UI via four minimal endpoints.
/// <list type="bullet">
///   <item><description><c>POST   /api/secrets/save</c>      — encrypt and persist a secret on behalf of the caller.</description></item>
///   <item><description><c>POST   /api/secrets/load</c>      — decrypt and return a previously saved secret.</description></item>
///   <item><description><c>GET    /api/secrets/{key}/exists</c> — check whether a secret file exists without decrypting it.</description></item>
///   <item><description><c>DELETE /api/secrets/{key}</c>     — permanently delete a stored secret file.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Passwords are <b>never</b> sent to these endpoints. The browser is responsible for hashing the user's
/// password (e.g. with SHA-256) before sending. The resulting <c>passwordHash</c> is forwarded here and
/// used by <see cref="ISecretProvider"/> to derive the AES encryption key via PBKDF2-SHA256.
/// </para>
/// <para>
/// This keeps the actual encrypted files isolated on the server's file system while allowing the browser
/// to optionally remember the hash — protecting against XSS and rudimentary credential stealers even
/// when the user enables the "remember password" option.
/// </para>
/// </remarks>
internal static class SecretsEndpoints
{
    internal static void MapSecretsEndpoints(this WebApplication app)
    {
        // ── Save ──────────────────────────────────────────────────────────────

        app.MapPost("/api/secrets/save", (SaveSecretRequest req, ISecretProvider secrets) =>
        {
            if (string.IsNullOrWhiteSpace(req.KeyName))
                return Results.BadRequest("keyName is required.");
            if (string.IsNullOrWhiteSpace(req.Secret))
                return Results.BadRequest("secret is required.");
            if (string.IsNullOrWhiteSpace(req.PasswordHash))
                return Results.BadRequest("passwordHash is required.");

            secrets.SaveSecret(req.KeyName, req.Secret, req.PasswordHash);
            return Results.Ok();
        });

        // ── Load ──────────────────────────────────────────────────────────────

        app.MapPost("/api/secrets/load", (LoadSecretRequest req, ISecretProvider secrets) =>
        {
            if (string.IsNullOrWhiteSpace(req.KeyName))
                return Results.BadRequest("keyName is required.");
            if (string.IsNullOrWhiteSpace(req.PasswordHash))
                return Results.BadRequest("passwordHash is required.");

            string? value = secrets.LoadSecret(req.KeyName, req.PasswordHash);

            // Return 401 when the file exists but decryption failed (wrong password)
            // and 404 when no secret has been saved for that key yet.
            if (value is null)
            {
                bool exists = secrets.SecretExists(req.KeyName);
                return exists ? Results.Unauthorized() : Results.NotFound();
            }

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
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

internal sealed record SaveSecretRequest(string KeyName, string Secret, string PasswordHash);
internal sealed record LoadSecretRequest(string KeyName, string PasswordHash);
