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
            public string ClickableText { get; } // New property
            public DateTime Timestamp { get; }

            public LogEntry(string message, LogLevel level, Exception exception = null, string clickablePath = null, string clickableText = null)
            {
                Message = message;
                Level = level;
                Exception = exception;
                ClickablePath = clickablePath;
                ClickableText = clickableText; // Assign new property
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

        private void LogInteractive(LogLevel level, string message, string clickablePath, string clickableText)
        {
            var logEntry = new LogEntry(message, level, clickablePath: clickablePath, clickableText: clickableText);
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

        public void LogInteractiveSuccess(string message, string clickablePath, string clickableText = null)
        {
            LogInteractive(LogLevel.Success, message, clickablePath, clickableText);
        }

        public void LogInteractiveWarning(string message, string clickablePath, string clickableText = null)
        {
            LogInteractive(LogLevel.Warning, message, clickablePath, clickableText);
        }

        public void LogInteractiveError(string message, string clickablePath, string clickableText = null)
        {
            LogInteractive(LogLevel.Error, message, clickablePath, clickableText);
        }

        public void LogInteractiveInfo(string message, string clickablePath, string clickableText = null)
        {
            LogInteractive(LogLevel.Info, message, clickablePath, clickableText);
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

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) }; // Slight spacing between lines

            Brush levelColor;
            string levelTag;

            // Helper to safe get resource
            Brush GetBrush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.White;

            switch (logEntry.Level)
            {
                case LogLevel.Info:
                    levelColor = GetBrush("AccentBrush"); // Blue-ish
                    levelTag = "Info";
                    break;
                case LogLevel.Warning:
                    levelColor = GetBrush("AccentOrange");
                    levelTag = "Warn";
                    break;
                case LogLevel.Error:
                    levelColor = GetBrush("AccentRed");
                    levelTag = "Error";
                    break;
                case LogLevel.Success:
                    levelColor = GetBrush("AccentGreen");
                    levelTag = "Success";
                    break;
                case LogLevel.Debug:
                    levelColor = Brushes.Gray;
                    levelTag = "Debug";
                    break;
                default:
                    levelColor = GetBrush("TextPrimary");
                    levelTag = "Unknown";
                    break;
            }

            // Time: 10:30:15
            var timestampRun = new Run($"{logEntry.Timestamp:HH:mm:ss}") { Foreground = GetBrush("TextSecondary") };
            
            // Separator:  │ 
            var separatorRun1 = new Run(" │ ") { Foreground = GetBrush("BorderColor") };
            
            // Level: Info
            var levelRun = new Run($"{levelTag}") { Foreground = levelColor, FontWeight = FontWeights.Medium };
            
            // Separador:  │ 
            var separatorRun2 = new Run(" │ ") { Foreground = GetBrush("BorderColor") };

            paragraph.Inlines.Add(timestampRun);
            paragraph.Inlines.Add(separatorRun1);
            paragraph.Inlines.Add(levelRun);
            paragraph.Inlines.Add(separatorRun2);

            if (!string.IsNullOrEmpty(logEntry.ClickablePath) && (File.Exists(logEntry.ClickablePath) || Directory.Exists(logEntry.ClickablePath)))
            {
                string linkText = string.IsNullOrEmpty(logEntry.ClickableText) ? logEntry.ClickablePath : logEntry.ClickableText;
                string fullMessage = logEntry.Message;

                // Find where the clickable text/path should be inserted in the message
                // For simplicity, we'll assume the clickable text is at the end of the message for now,
                // or we'll just append it if not found.
                int pathIndex = fullMessage.IndexOf(linkText, StringComparison.OrdinalIgnoreCase);
                if (pathIndex == -1)
                {
                    pathIndex = fullMessage.IndexOf(logEntry.ClickablePath, StringComparison.OrdinalIgnoreCase);
                }

                if (pathIndex != -1)
                {
                    // Text before the path
                    paragraph.Inlines.Add(new Run(fullMessage.Substring(0, pathIndex)) { Foreground = GetBrush("TextPrimary") });

                    // The hyperlink
                    var link = new Hyperlink(new Run(linkText));
                    link.Foreground = GetBrush("AccentBrush");
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
                    paragraph.Inlines.Add(new Run(fullMessage.Substring(pathIndex + linkText.Length)) { Foreground = GetBrush("TextPrimary") });
                }
                else
                {
                    // If the linkText/ClickablePath is not found in the message, just append the message and the link
                    paragraph.Inlines.Add(new Run(fullMessage + " ") { Foreground = GetBrush("TextPrimary") });
                    var link = new Hyperlink(new Run(linkText));
                    link.Foreground = GetBrush("AccentBrush");
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
                }
            }
            else
            {
                paragraph.Inlines.Add(new Run(logEntry.Message) { Foreground = GetBrush("TextPrimary") });
            }

            if (logEntry.Exception != null)
            {
                var exceptionDetailRun = new Run(" See application_errors.log for more details.")
                {
                    Foreground = GetBrush("AccentOrange"),
                    FontStyle = FontStyles.Italic
                };
                paragraph.Inlines.Add(exceptionDetailRun);
            }

            _outputRichTextBox.Document.Blocks.Add(paragraph);
            _outputRichTextBox.ScrollToEnd();
        }
    }
}
