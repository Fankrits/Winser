using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Winser.Services;

/// <summary>
/// A tiny JSON-file-backed document store: synchronous load at startup, debounced atomic
/// writes afterwards so a burst of edits (typing in settings, recording history) collapses
/// into one disk write.
/// </summary>
public sealed class JsonStore<T> : IDisposable
    where T : class
{
    private const int DebounceMilliseconds = 750;

    private readonly string _path;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T> _factory;
    private readonly object _gate = new();
    private readonly Timer _timer;

    private T? _pending;
    private bool _disposed;

    public JsonStore(string fileName, JsonTypeInfo<T> typeInfo, Func<T> factory)
    {
        _path = AppPaths.DataFile(fileName);
        _typeInfo = typeInfo;
        _factory = factory;
        _timer = new Timer(_ => Flush(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    public T Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return _factory();
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, _typeInfo) ?? _factory();
        }
        catch (JsonException ex)
        {
            // Keep the bad file around instead of silently throwing the user's data away.
            Debug.WriteLine($"[Winser] {_path} is not valid JSON: {ex.Message}");
            TryQuarantine();
            return _factory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Winser] Could not read {_path}: {ex.Message}");
            return _factory();
        }
    }

    /// <summary>Queues a write; repeated calls within the debounce window coalesce.</summary>
    public void Save(T value)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = value;
            _timer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>Writes any queued value immediately. Called on shutdown.</summary>
    public void Flush()
    {
        T? value;
        lock (_gate)
        {
            value = _pending;
            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (value is null)
        {
            return;
        }

        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, value, _typeInfo);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Winser] Could not write {_path}: {ex.Message}");
            TryDelete(temp);
        }
    }

    public void Dispose()
    {
        Flush();
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void TryQuarantine()
    {
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
