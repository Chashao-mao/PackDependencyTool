using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackDepTool.Core.Persistence;

/// <summary>Stores the CurseForge API key outside of settings / logs / crash reports.</summary>
public interface ISecretStore
{
    string Description { get; }
    Task<bool> HasCurseForgeKeyAsync(CancellationToken cancellationToken = default);
    Task<string?> GetCurseForgeKeyAsync(CancellationToken cancellationToken = default);
    Task SetCurseForgeKeyAsync(string key, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public static class SecretStoreFactory
{
    public static ISecretStore Create(AppPaths paths) =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretStore(paths)
            : new AesFileSecretStore(paths);
}

/// <summary>Windows DPAPI (CurrentUser scope). The ciphertext is portable only for the same user/machine.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiSecretStore(AppPaths paths) => _paths = paths;

    public string Description => "Windows DPAPI（当前用户）";

    public Task<bool> HasCurseForgeKeyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(GetCurseForgeKeyCore()));

    public Task<string?> GetCurseForgeKeyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(GetCurseForgeKeyCore());

    private string? GetCurseForgeKeyCore()
    {
        var file = ReadFile();
        if (file is null || string.IsNullOrWhiteSpace(file.CurseForgeKey)) return null;

        try
        {
            var encrypted = Convert.FromBase64String(file.CurseForgeKey);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public async Task SetCurseForgeKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API Key 不能为空", nameof(key));

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);

        var file = new SecretFile
        {
            Version = 1,
            CurseForgeKey = Convert.ToBase64String(encrypted),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            await AtomicFile.WriteTextAsync(
                    _paths.SecretsFilePath, JsonSerializer.Serialize(file), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.SecretsFilePath)) File.Delete(_paths.SecretsFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SecretFile? ReadFile()
    {
        var text = AtomicFile.TryReadText(_paths.SecretsFilePath);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<SecretFile>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class SecretFile
    {
        public int Version { get; set; }
        public string? CurseForgeKey { get; set; }
    }
}

/// <summary>
/// Non-Windows fallback: AES-256-GCM with a random key file that is chmod 600.
/// Keeps secrets out of plain text without pretending to offer DPAPI's guarantees.
/// </summary>
public sealed class AesFileSecretStore : ISecretStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AesFileSecretStore(AppPaths paths) => _paths = paths;

    public string Description => "本地 AES-256-GCM 加密（密钥文件权限 600）";

    public Task<bool> HasCurseForgeKeyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(GetCurseForgeKeyCore()));

    public Task<string?> GetCurseForgeKeyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(GetCurseForgeKeyCore());

    private string? GetCurseForgeKeyCore()
    {
        var text = AtomicFile.TryReadText(_paths.SecretsFilePath);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("curseForgeKey", out var node) ||
                node.ValueKind != JsonValueKind.String)
                return null;

            var payload = Convert.FromBase64String(node.GetString()!);
            if (payload.Length < 12 + 16) return null;

            var key = GetOrCreateKey();
            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var cipher = payload.AsSpan(28);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public async Task SetCurseForgeKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API Key 不能为空", nameof(key));

        var plain = Encoding.UTF8.GetBytes(key.Trim());
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];

        using (var aes = new AesGcm(GetOrCreateKey(), 16))
            aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, 12);
        cipher.CopyTo(payload, 28);

        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            curseForgeKey = Convert.ToBase64String(payload),
        });

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            await AtomicFile.WriteTextAsync(_paths.SecretsFilePath, json, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.SecretsFilePath)) File.Delete(_paths.SecretsFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_paths.SecretKeyFilePath))
        {
            var existing = File.ReadAllBytes(_paths.SecretKeyFilePath);
            if (existing.Length == 32) return existing;
        }

        _paths.EnsureDirectories();
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_paths.SecretKeyFilePath, key);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    _paths.SecretKeyFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Best effort; the data directory permissions still apply.
            }
        }
        return key;
    }
}

/// <summary>
/// Reads the CurseForge key from the environment first (never persisted), then the encrypted store.
/// The key is only exposed to the HTTP client.
/// </summary>
public sealed class ApiKeyProvider
{
    public const string EnvironmentVariable = "CURSEFORGE_API_KEY";

    private readonly ISecretStore _store;

    public ApiKeyProvider(ISecretStore store) => _store = store;

    public bool IsFromEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public string StorageDescription => _store.Description;

    public async Task<string?> GetCurseForgeKeyAsync(CancellationToken cancellationToken = default)
    {
        var environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();
        return await _store.GetCurseForgeKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCurseForgeKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("API Key 不能为空", nameof(key));
        await _store.SetCurseForgeKeyAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => _store.ClearAsync(cancellationToken);

    public Task<bool> HasStoredKeyAsync(CancellationToken cancellationToken = default) =>
        _store.HasCurseForgeKeyAsync(cancellationToken);
}
