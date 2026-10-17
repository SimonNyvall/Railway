using System.IO;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Engine.Orchestration;

/// <summary>
/// Production <see cref="IWatchEventSource"/> backed by a recursive <see cref="FileSystemWatcher"/>.
/// Portable across Windows/macOS/Linux; the only OS nuance (gitignore checks) is handled by
/// <see cref="HookWatcher"/> through the injectable command runner.
/// </summary>
internal sealed class FileSystemWatcherWatchEventSource : IWatchEventSource
{
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<WatchPathChange>? Changed;

    /// <inheritdoc />
    public void Start(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        Stop();

        var watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    /// <inheritdoc />
    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;

        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnChanged;
        watcher.Created -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Changed = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Raise(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args) => Raise(args.FullPath);

    private void OnError(object sender, ErrorEventArgs args)
    {
        // A watcher error (e.g. internal buffer overflow) is not fatal; the loop keeps running.
    }

    private void Raise(string fullPath)
    {
        var handler = Changed;

        if (handler is null)
        {
            return;
        }

        bool isDirectory;

        try
        {
            isDirectory = Directory.Exists(fullPath) && !File.Exists(fullPath);
        }
        catch (Exception)
        {
            isDirectory = false;
        }

        try
        {
            handler(new WatchPathChange(fullPath, isDirectory));
        }
        catch (Exception)
        {
            // A misbehaving handler must never kill the watcher thread.
        }
    }
}