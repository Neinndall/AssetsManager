using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls; // Added
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace AssetsManager.Services.Core
{
    public class LogService
    {
        private RichTextBox _outputRichTextBox;
        private readonly Dispatcher _dispatcher;
        private readonly ILogger _logger;

        private readonly Queue<LogEntry> _pendingLogs = new Queue<LogEntry>();

        public enum LogLevel
        {
            Info,
            Warning,
            Error,
            Success,
            Debug
        }

        public class LogEntry
        {
            public string Message { get; }
            public LogLevel Level { get; }
            public Exception Exception { get; }
            public string ClickablePath { get; }
            public DateTime Timestamp { get; }

            public LogEntry(string message, LogLevel level, Exception exception = null, string clickablePath = null)
            {
                Message = message;
                Level = level;
                Exception = exception;
                ClickablePath = clickablePath;
                Timestamp = DateTime.Now;
            }
        }

        public LogService(ILogger logger)
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _logger = logger;
        }

        public void SetLogOutput(RichTextBox outputRichTextBox, bool preserveExistingLogs = false)
        {
            _outputRichTextBox = outputRichTextBox;

            _dispatcher.Invoke(() =>
            {
                if (_outputRichTextBox.Document == null || _outputRichTextBox.Document.Blocks.Count == 0 || !preserveExistingLogs)
                {
                    _outputRichTextBox.Document = new FlowDocument();
                }

                while (_pendingLogs.TryDequeue(out var logEntry))
                {
                    WriteLog(logEntry);
                }
            });
        }

        public void ClearLog()
        {
            _dispatcher.Invoke(() =>
            {
                if (_outputRichTextBox != null)
                {
                    _outputRichTextBox.Document = new FlowDocument();
                }
            });
        }

        public void Log(string message)
        {
            _logger.Information(message);
            WriteLog(new LogEntry(message, LogLevel.Info));
        }

        public void LogWarning(string message)
        {
            _logger.Warning(message);
            WriteLog(new LogEntry(message, LogLevel.Warning));
        }

        public void LogError(string message)
        {
            _logger.Error(message);
            WriteLog(new LogEntry(message, LogLevel.Error));
        }

        public void LogSuccess(string message)
        {
            _logger.Information(message);
            WriteLog(new LogEntry(message, LogLevel.Success));
        }

        public void LogDebug(string message)
        {
            _logger.Debug(message);
            WriteLog(new LogEntry(message, LogLevel.Debug));
        }

        public void LogError(Exception ex, string message)
        {
            _logger.Error(ex, message);
            WriteLog(new LogEntry(message, LogLevel.Error, ex));
        }

        public void LogCritical(Exception ex, string message)
        {
            _logger.Fatal(ex, message);
            // This method now only logs to the fatal error file, not to the UI.
        }

        private void LogInteractive(LogLevel level, string message, string clickablePath)
        {
            var logEntry = new LogEntry(message, level, clickablePath: clickablePath);
            switch (level)
            {
                case LogLevel.Warning:
                    _logger.Warning(message);
                    break;
                case LogLevel.Error:
                    _logger.Error(message);
                    break;
                default:
                    _logger.Information(message);
                    break;
            }
            WriteLog(logEntry);
        }

        public void LogInteractiveSuccess(string message, string clickablePath)
        {
            LogInteractive(LogLevel.Success, message, clickablePath);
        }

        public void LogInteractiveWarning(string message, string clickablePath)
        {
            LogInteractive(LogLevel.Warning, message, clickablePath);
        }

        public void LogInteractiveError(string message, string clickablePath)
        {
            LogInteractive(LogLevel.Error, message, clickablePath);
        }

        public void LogInteractiveInfo(string message, string clickablePath)
        {
            LogInteractive(LogLevel.Info, message, clickablePath);
        }

        private void WriteLog(LogEntry logEntry)
        {
            if (_outputRichTextBox == null)
            {
                _pendingLogs.Enqueue(logEntry);
                System.Diagnostics.Debug.WriteLine($"[PENDING LOG (early)] [{logEntry.Level}] {logEntry.Message}{(logEntry.Exception != null ? $" Exception: {logEntry.Exception.Message}" : "")}");
                return;
            }

            if (logEntry.Level == LogLevel.Debug)
            {
                return;
            }

            _dispatcher.Invoke(() =>
            {
                AppendToLog(logEntry);
            });
        }

        private void AppendToLog(LogEntry logEntry)
        {
            if (_outputRichTextBox == null) return;

            var paragraph = new Paragraph { Margin = new Thickness(0) };

            SolidColorBrush levelColor;
            string levelTag;

            switch (logEntry.Level)
            {
                case LogLevel.Info:
                    levelColor = Brushes.Green;
                    levelTag = "INFO";
                    break;
                case LogLevel.Warning:
                    levelColor = Brushes.Yellow;
                    levelTag = "WARNING";
                    break;
                case LogLevel.Error:
                    levelColor = Brushes.Red;
                    levelTag = "ERROR";
                    break;
                case LogLevel.Success:
                    levelColor = Brushes.LightGreen;
                    levelTag = "SUCCESS";
                    break;
                case LogLevel.Debug:
                    levelColor = Brushes.LightBlue;
                    levelTag = "DEBUG";
                    break;
                default:
                    levelColor = Brushes.White;
                    levelTag = "UNKNOWN";
                    break;
            }

            var timestampRun = new Run($"[{logEntry.Timestamp:HH:mm:ss}] ") { Foreground = Brushes.LightGray };
            var levelRun = new Run($"[{levelTag}] ") { Foreground = levelColor, FontWeight = FontWeights.Medium };

            paragraph.Inlines.Add(timestampRun);
            paragraph.Inlines.Add(levelRun);

            if (!string.IsNullOrEmpty(logEntry.ClickablePath) && (File.Exists(logEntry.ClickablePath) || Directory.Exists(logEntry.ClickablePath)))
            {
                int pathIndex = logEntry.Message.IndexOf(logEntry.ClickablePath, StringComparison.OrdinalIgnoreCase);
                if (pathIndex != -1)
                {
                    // Text before the path
                    paragraph.Inlines.Add(new Run(logEntry.Message.Substring(0, pathIndex)));

                    // The hyperlink
                    var link = new Hyperlink(new Run(logEntry.ClickablePath));
                    link.Foreground = Application.Current.FindResource("AccentBrush") as SolidColorBrush;
                    link.NavigateUri = new Uri(Path.GetFullPath(logEntry.ClickablePath));
                    link.RequestNavigate += (sender, e) =>
                    {

                        try
                        {
                            Process.Start("explorer.exe", $"\"{e.Uri.LocalPath}\"");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Failed to open file path from log.");
                        }
                        e.Handled = true;
                    };
                    paragraph.Inlines.Add(link);

                    // Text after the path
                    paragraph.Inlines.Add(new Run(logEntry.Message.Substring(pathIndex + logEntry.ClickablePath.Length)));
                }
                else
                {
                    paragraph.Inlines.Add(new Run(logEntry.Message));
                }
            }
            else
            {
                paragraph.Inlines.Add(new Run(logEntry.Message));
            }

            if (logEntry.Exception != null)
            {
                var exceptionDetailRun = new Run(" See application_errors.log for more details.")
                {
                    Foreground = Brushes.OrangeRed,
                    FontStyle = FontStyles.Italic
                };
                paragraph.Inlines.Add(exceptionDetailRun);
            }

            _outputRichTextBox.Document.Blocks.Add(paragraph);
            _outputRichTextBox.ScrollToEnd();
        }
    }
}
