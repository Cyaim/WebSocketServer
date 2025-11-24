using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Example.Wpf
{
    public class WebSocketLogger : ILogger
    {
        private readonly string _name;
        private readonly WebSocketLoggerProvider _provider;

        public WebSocketLogger(string name, WebSocketLoggerProvider provider)
        {
            _name = name;
            _provider = provider;
        }

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.LogLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            _provider.WriteLog(_name, logLevel, eventId, message, exception);
        }
    }
}
