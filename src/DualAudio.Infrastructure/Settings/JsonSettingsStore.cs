using System.Text.Json;
using System.Text.Json.Serialization;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Infrastructure.Settings;

/// <summary>
/// <see cref="AppSettings"/> as JSON in one file (normally %LOCALAPPDATA%\DualAudio\settings.json).
/// <list type="bullet">
/// <item>Save is atomic: the JSON goes to "settings.json.tmp" (flushed to disk), then File.Replace swaps it in, so a
/// crash mid-write leaves either the old or the new file, never a half-written one.</item>
/// <item>Load never throws for a bad file: a missing file gives defaults; an unparsable one is renamed to
/// "settings.json.corrupt-&lt;UTC timestamp&gt;.bak" and defaults are returned (and written on the next save).</item>
/// <item>Load and Save are serialized within the process.</item>
/// </list>
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore>? logger = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath => _path;

    private string TempPath => _path + ".tmp";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings.Sanitize() with { SchemaVersion = AppSettings.CurrentSchemaVersion }, JsonOptions);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceWithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No settings file yet; using defaults");
            return AppSettings.Default;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable right now: don't treat it as corrupt (that would discard a good file).
            _logger.LogWarning(ex, "Settings file could not be read; using defaults for this session");
            return AppSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(bytes, JsonOptions)
                ?? throw new JsonException("settings file contains null");
            return settings.Sanitize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            BackUpCorruptFile(ex);
            return AppSettings.Default;
        }
    }

    private void BackUpCorruptFile(Exception reason)
    {
        var backup = $"{_path}.corrupt-{_time.GetUtcNow():yyyyMMdd-HHmmss}.bak";
        try
        {
            File.Move(_path, backup, overwrite: true);
            _logger.LogWarning("Settings file was corrupt ({Error}); moved to {Backup} and using defaults", reason.GetType().Name, Path.GetFileName(backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file was corrupt and could not be backed up; using defaults");
        }
    }

    private async Task ReplaceWithRetryAsync(CancellationToken cancellationToken)
    {
        // A virus scanner or indexer may hold the target for a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Replace(TempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(TempPath, _path);
                }

                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Replacing the settings file failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
