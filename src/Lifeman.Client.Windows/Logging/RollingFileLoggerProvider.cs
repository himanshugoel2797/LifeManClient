using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Windows.Logging;

/// Minimal day-rolling file logger. Owns a single background writer task
/// so log calls never block — important when collectors and the SSE loop
/// share a thread pool with the writer's I/O. Keeps the last `RetainDays`
/// files; old ones are deleted on rollover.
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Task _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private DateOnly _currentDay;
    private FileStream? _stream;
    private StreamWriter? _streamWriter;

    public RollingFileLoggerProvider(string directory, int retainDays = 7)
    {
        _directory = directory;
        _retainDays = retainDays;
        Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, n => new RollingFileLogger(n, this));

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    EnsureStreamForToday();
                    _streamWriter!.WriteLine(line);
                    _streamWriter.Flush();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[file-log] write failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[file-log] writer crashed: {ex.Message}");
        }
        finally
        {
            try { _streamWriter?.Flush(); _streamWriter?.Dispose(); } catch { }
        }
    }

    private void EnsureStreamForToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_streamWriter is not null && today == _currentDay) return;

        try { _streamWriter?.Flush(); _streamWriter?.Dispose(); } catch { }

        _currentDay = today;
        var path = Path.Combine(_directory, $"client-{today:yyyy-MM-dd}.log");
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _streamWriter = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = false };
        TrimOld();
    }

    private void TrimOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var f in Directory.EnumerateFiles(_directory, "client-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) { try { File.Delete(f); } catch { } }
            }
        }
        catch { /* logging cleanup is best-effort */ }
    }

    public void Dispose()
    {
        // Complete the channel and let the writer drain naturally
        // before pulling the cancel — otherwise the writer may exit
        // mid-batch and lose buffered log lines.
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly string _category;
        private readonly RollingFileLoggerProvider _provider;
        public RollingFileLogger(string category, RollingFileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var msg = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:O} {Short(level)} {_category}: {msg}";
            if (exception is not null) line += " | " + exception;
            _provider.Enqueue(line);
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
