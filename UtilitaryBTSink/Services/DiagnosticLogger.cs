using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UtilitaryBTSink.Services;

public sealed class DiagnosticLogger
{
    private const int MaxLinesInMemory = 400;
    private const string CurrentLogFileName = "current-session.log";
    private const string PreviousLogFileName = "last-session.log";

    private readonly object _sync = new();
    private readonly Queue<string> _lines = new();

    public string LogDirectory { get; }
    public string LogFilePath { get; }
    public string PreviousLogFilePath { get; }

    public event Action? LogUpdated;

    public DiagnosticLogger()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UtilitaryBTSink",
            "logs");
        Directory.CreateDirectory(LogDirectory);

        LogFilePath = Path.Combine(LogDirectory, CurrentLogFileName);
        PreviousLogFilePath = Path.Combine(LogDirectory, PreviousLogFileName);

        RotateAndCleanOldLogs();
    }

    private void RotateAndCleanOldLogs()
    {
        try
        {
            if (File.Exists(LogFilePath))
            {
                var info = new FileInfo(LogFilePath);
                if (info.Length > 0)
                {
                    File.Move(LogFilePath, PreviousLogFilePath, overwrite: true);
                }
                else
                {
                    File.Delete(LogFilePath);
                }
            }

            foreach (string file in Directory.GetFiles(LogDirectory, "*.log"))
            {
                string name = Path.GetFileName(file);
                if (!string.Equals(name, CurrentLogFileName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, PreviousLogFileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
            // Never block app startup if a log file is locked in an external editor
        }
    }

    public void Log(string category, string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}";

        lock (_sync)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLinesInMemory)
            {
                _lines.Dequeue();
            }

            try
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Never crash the app if the file is externally locked
            }
        }

        LogUpdated?.Invoke();
    }

    public string GetFullLogText()
    {
        lock (_sync)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }

    public string GetPreviousLogText()
    {
        try
        {
            if (File.Exists(PreviousLogFilePath))
            {
                return File.ReadAllText(PreviousLogFilePath, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore read failure if the file is externally locked
        }
        return "No previous session recorded yet.";
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
            try
            {
                File.WriteAllText(LogFilePath, string.Empty, Encoding.UTF8);
            }
            catch { }
        }
        Log("LOG", "Log buffer cleared by user.");
    }
}
