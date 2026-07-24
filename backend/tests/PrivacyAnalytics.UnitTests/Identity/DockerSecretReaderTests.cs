using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrivacyAnalytics.Infrastructure.Identity;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Identity;

/// <summary>
/// Proves <see cref="DockerSecretReader"/> correctly materializes a Docker secret file: it
/// round-trips hex content, trims trailing whitespace/newlines Docker adds, returns null when the
/// file is absent, and rejects empty secret names.
/// </summary>
public sealed class DockerSecretReaderTests : IDisposable
{
    private readonly string _secretsDir = Path.Combine(Path.GetTempPath(), "pa-secrets-" + Guid.NewGuid().ToString("N"));

    public DockerSecretReaderTests() => Directory.CreateDirectory(_secretsDir);

    public void Dispose()
    {
        if (Directory.Exists(_secretsDir))
        {
            Directory.Delete(_secretsDir, recursive: true);
        }
    }

    private IOptionsMonitor<IdentityOptions> Options() =>
        IdentityTestOptions.Create(secretsPath: _secretsDir);

    private void WriteSecret(string name, string content) =>
        File.WriteAllText(Path.Combine(_secretsDir, name), content);

    [Fact]
    public void ReadsHexSecret_AndTrimsTrailingNewline()
    {
        var hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        WriteSecret("analytics_durable_hmac_key", hex + "\n");

        var reader = new DockerSecretReader(Options(), NullLogger<DockerSecretReader>.Instance);

        var bytes = reader.TryReadSecret("analytics_durable_hmac_key");

        Assert.NotNull(bytes);
        Assert.Equal(Convert.FromHexString(hex), bytes);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var reader = new DockerSecretReader(Options(), NullLogger<DockerSecretReader>.Instance);
        Assert.Null(reader.TryReadSecret("does-not-exist"));
    }

    [Fact]
    public void EmptySecretName_Throws()
    {
        var reader = new DockerSecretReader(Options(), NullLogger<DockerSecretReader>.Instance);
        Assert.Throws<ArgumentException>(() => reader.TryReadSecret(""));
    }

    [Fact]
    public void ZeroLengthSecret_ReturnsNull()
    {
        WriteSecret("empty", "");
        var reader = new DockerSecretReader(Options(), NullLogger<DockerSecretReader>.Instance);
        Assert.Null(reader.TryReadSecret("empty"));
    }

    [Fact]
    public void NonHexContent_Throws()
    {
        WriteSecret("not-hex", "this-is-not-hex");
        var reader = new DockerSecretReader(Options(), NullLogger<DockerSecretReader>.Instance);
        Assert.Throws<FormatException>(() => reader.TryReadSecret("not-hex"));
    }
}
