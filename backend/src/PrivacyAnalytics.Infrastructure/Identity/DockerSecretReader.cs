using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrivacyAnalytics.Infrastructure.Identity;

/// <summary>
/// Reads secret bytes from files mounted into the container by the Docker secrets mechanism
/// (or, in hosted deployments, a managed-secrets sidecar that materializes the same file paths).
/// This is the out-of-band channel for the HMAC signing key: the key never lives in the database,
/// in appsettings, or in an environment variable holding the literal value (FR-2.1).
/// </summary>
public interface ISecretReader
{
    /// <summary>
    /// Reads the raw bytes of the named secret. Returns <c>null</c> when the file is absent,
    /// so callers can apply their own fallback / fail-fast policy.
    /// </summary>
    byte[]? TryReadSecret(string name);
}

/// <summary>
/// File-backed <see cref="ISecretReader"/>. Docker mounts each declared secret as a regular file
/// under <see cref="IdentityOptions.SecretsPath"/>/<c>&lt;name&gt;</c>; the file contents are the
/// secret bytes with a trailing newline trimmed.
/// </summary>
public sealed class DockerSecretReader(IOptionsMonitor<IdentityOptions> options, ILogger<DockerSecretReader> logger)
    : ISecretReader
{
    public byte[]? TryReadSecret(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Secret name must be a non-empty string.", nameof(name));
        }

        var opts = options.CurrentValue;
        var path = Path.Combine(opts.SecretsPath, name);

        if (!File.Exists(path))
        {
            return null;
        }

        // Docker secret files are typically world-readable and contain a trailing newline; we trim
        // ASCII whitespace/newlines so the effective key bytes are deterministic regardless of how
        // the operator authored the file.
        var text = File.ReadAllText(path).Trim('\n', '\r', ' ', '\t');
        var bytes = Convert.FromHexString(text);
        if (bytes.Length == 0)
        {
            logger.LogWarning("Secret file '{Path}' was present but decoded to zero bytes.", path);
            return null;
        }
        return bytes;
    }
}
