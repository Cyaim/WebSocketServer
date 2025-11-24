using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Example.Wpf
{
    public class WebSocketLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, WebSocketLogger> _loggers = new ConcurrentDictionary<string, WebSocketLogger>();
        private readonly Action<string> _logAction;

        public LogLevel LogLevel { get; }

        public WebSocketLoggerProvider(LogLevel logLevel, Action<string> logAction)
        {
            LogLevel = logLevel;
            _logAction = logAction;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new WebSocketLogger(name, this));
        }

        public void Dispose()
        {
            _loggers.Clear();
        }

        public void WriteLog(string name, LogLevel logLevel, EventId eventId, string message, Exception? exception)
        {
            _logAction($"{logLevel}: {name}: {message}");
        }
    }
}
