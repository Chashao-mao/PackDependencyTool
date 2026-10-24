using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PackDepTool.Core.Net;

namespace PackDepTool.App.Services;

public sealed class UpdateDownloadResult
{
    public bool Succeeded { get; init; }
    public string? FilePath { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Downloads an update only when the manifest carries a SHA-256 hash. The
/// executable is hashed after download; an in-app updater copy then waits for
/// the main process to exit, backs up the old exe and replaces it atomically.
/// This is deliberately opt-in.
/// </summary>
public sealed class UpdateInstaller
{
    // Pinned signer for releases produced by this project. Self-signed releases
    // are not trusted by Windows, but this pin prevents a different signer from
    // being substituted through a compromised update feed.
    private const string ExpectedSignerThumbprint =
        "132BDA330ACF56C47D095F50C5A395A2CB9A9D5A";

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public UpdateInstaller(HttpClient http, string userAgent)
    {
        _http = http;
        _userAgent = userAgent;
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateCheckResult update,
        string version,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateDownloadResult { Error = "更新文件地址不是 HTTPS。" };
        if (!IsSha256(update.Sha256))
            return new UpdateDownloadResult { Error = "更新清单缺少有效的 SHA256，拒绝自动执行。" };

        var safeVersion = string.Concat(version.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeVersion)) safeVersion = "latest";
        var directory = Path.Combine(Path.GetTempPath(), "PackDependencyTool-update", safeVersion);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "PackDependencyTool.exe");
        var temp = target + ".download";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateDownloadResult { Error = $"下载更新失败（HTTP {(int)response.StatusCode}）。" };

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    readTotal += read;
                    if (total is > 0) progress?.Report((double)readTotal / total.Value);
                }
            }

            var actual = await ComputeSha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, update.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                return new UpdateDownloadResult { Error = "更新文件 SHA256 校验失败，已删除下载文件。" };
            }

            if (!HasExpectedSigner(temp))
            {
                File.Delete(temp);
                return new UpdateDownloadResult
                {
                    Error = "更新文件 Authenticode 签名者与本项目不匹配，已拒绝执行。",
                };
            }

            File.Move(temp, target, true);
            progress?.Report(1);
            return new UpdateDownloadResult { Succeeded = true, FilePath = target };
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return new UpdateDownloadResult { Error = "更新下载已取消。" };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new UpdateDownloadResult { Error = $"下载更新失败：{ex.Message}" };
        }
    }

    /// <summary>
    /// Starts a helper that waits for this process to exit, replaces the exe,
    /// then starts the new exe. Returns false when the helper cannot be safely
    /// started; the caller must not exit in that case.
    /// </summary>
    public bool StartSelfUpdate(string downloadedPath)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) ||
            !current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(current) || !File.Exists(downloadedPath))
            return false;
        if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(downloadedPath),
                StringComparison.OrdinalIgnoreCase))
            return false;

        var directory = Path.Combine(Path.GetTempPath(), "PackDependencyTool-update");
        Directory.CreateDirectory(directory);
        var updater = Path.Combine(directory, $"updater-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(current, updater, true);
            Process.Start(new ProcessStartInfo
            {
                FileName = updater,
                Arguments = "--apply-update " +
                            $"--old \"{current}\" --new \"{Path.GetFullPath(downloadedPath)}\" " +
                            $"--pid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(current)!,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TryDelete(updater);
            return false;
        }
    }

    /// <summary>Runs only in the temporary updater copy, before Avalonia starts.</summary>
    public static int RunApplyUpdate(string[] args)
    {
        string? Value(string name)
        {
            for (var index = 0; index < args.Length - 1; index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            return null;
        }

        var oldPath = Value("--old");
        var newPath = Value("--new");
        var pidText = Value("--pid");
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath) ||
            !int.TryParse(pidText, out var pid) || !File.Exists(oldPath) || !File.Exists(newPath))
            return 2;

        var logDirectory = Path.Combine(Path.GetDirectoryName(oldPath)!, "data", "logs");
        Directory.CreateDirectory(logDirectory);
        void Log(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(logDirectory, "update.log"),
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }

        try
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(30_000))
                {
                    Log("old process did not exit within 30 seconds");
                    return 3;
                }
            }
            catch (ArgumentException)
            {
                // It already exited.
            }

            var staged = oldPath + ".new";
            var backup = oldPath + ".bak";
            File.Copy(newPath, staged, true);
            if (File.Exists(backup)) File.Delete(backup);

            try
            {
                File.Replace(staged, oldPath, backup, true);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                // Fallback for filesystems that do not support File.Replace.
                File.Move(staged, oldPath, true);
            }

            Log("update applied; starting new version");
            Process.Start(new ProcessStartInfo
            {
                FileName = oldPath,
                WorkingDirectory = Path.GetDirectoryName(oldPath)!,
                UseShellExecute = false,
            });
            TryDelete(staged);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log($"update failed: {ex.Message}");
            var backup = oldPath + ".bak";
            if (!File.Exists(oldPath) && File.Exists(backup))
            {
                try { File.Copy(backup, oldPath, true); }
                catch (IOException) { }
            }
            return 4;
        }
    }

    private static bool HasExpectedSigner(string path)
    {
        try
        {
            var certificate = X509Certificate.CreateFromSignedFile(path);
            using var signer = new X509Certificate2(certificate);
            return string.Equals(signer.Thumbprint, ExpectedSignerThumbprint,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(ch => Uri.IsHexDigit(ch));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
