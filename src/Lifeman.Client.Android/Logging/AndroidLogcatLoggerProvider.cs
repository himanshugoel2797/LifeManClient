using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Android.Logging;

/// Routes ILogger output to Android logcat under a fixed tag, so the
/// shared core's logs are visible alongside the heads' own logs.
public sealed class AndroidLogcatLoggerProvider : ILoggerProvider
{
    private readonly string _tag;
    public AndroidLogcatLoggerProvider(string tag = "lifeman") => _tag = tag;
    public ILogger CreateLogger(string categoryName) => new AndroidLogcatLogger(_tag, categoryName);
    public void Dispose() { }

    private sealed class AndroidLogcatLogger : ILogger
    {
        private readonly string _tag;
        private readonly string _category;
        public AndroidLogcatLogger(string tag, string category) { _tag = tag; _category = category; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var msg = $"{_category}: {formatter(state, exception)}";
            if (exception is not null) msg += " | " + exception;
            switch (level)
            {
                case LogLevel.Critical:
                case LogLevel.Error: global::Android.Util.Log.Error(_tag, msg); break;
                case LogLevel.Warning: global::Android.Util.Log.Warn(_tag, msg); break;
                case LogLevel.Information: global::Android.Util.Log.Info(_tag, msg); break;
                default: global::Android.Util.Log.Debug(_tag, msg); break;
            }
        }
    }
}
