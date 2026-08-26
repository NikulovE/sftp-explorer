using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace SftpExplorerWinUI
{
    public static class Log
    {
        private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SftpExplorer", "logs");
        private static readonly string LogFile = Path.Combine(LogDir, "app.log");
        private static readonly object _lock = new();
        private static bool _initialized = false;
        
        // Максимальный размер одного лог файла (5 МБ)
        private const long MaxLogFileSize = 5 * 1024 * 1024;
        // Максимальное количество архивных файлов
        private const int MaxArchiveFiles = 3;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                _initialized = true;
            }
            catch
            {
                // Не можем создать директорию - работаем без файлового логирования
            }
        }

        public static void Debug(string message, [CallerMemberName] string? caller = null)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss.fff} [{caller}] {message}");
#endif
        }

        public static void Info(string message, [CallerMemberName] string? caller = null)
        {
            var logLine = $"[INFO] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{caller}] {message}";
#if DEBUG
            System.Diagnostics.Debug.WriteLine(logLine);
#else
            WriteToFile(logLine);
#endif
        }

        public static void Warning(string message, Exception? ex = null, [CallerMemberName] string? caller = null)
        {
            var logLine = ex != null 
                ? $"[WARN] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{caller}] {message}\nException: {ex}" 
                : $"[WARN] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{caller}] {message}";
#if DEBUG
            System.Diagnostics.Debug.WriteLine(logLine);
#else
            WriteToFile(logLine);
#endif
        }

        public static void Error(string message, Exception? ex = null, [CallerMemberName] string? caller = null)
        {
            var logLine = ex != null 
                ? $"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{caller}] {message}\nException: {ex}" 
                : $"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{caller}] {message}";
#if DEBUG
            System.Diagnostics.Debug.WriteLine(logLine);
#else
            WriteToFile(logLine);
#endif
        }

        private static void WriteToFile(string line)
        {
            try
            {
                EnsureInitialized();
                if (!_initialized) return;
                
                lock (_lock)
                {
                    // Проверяем размер файла перед записью
                    RotateLogIfNeeded();
                    
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // Игнорируем ошибки логирования, чтобы не крашить приложение
            }
        }

        private static void RotateLogIfNeeded()
        {
            try
            {
                if (!File.Exists(LogFile)) return;
                
                var fileInfo = new FileInfo(LogFile);
                if (fileInfo.Length < MaxLogFileSize) return;
                
                // Удаляем самый старый архив, если достигнут лимит
                var oldestArchive = Path.Combine(LogDir, $"app.log.{MaxArchiveFiles}");
                if (File.Exists(oldestArchive))
                {
                    File.Delete(oldestArchive);
                }
                
                // Сдвигаем все архивы на одну позицию
                for (int i = MaxArchiveFiles - 1; i >= 1; i--)
                {
                    var currentArchive = Path.Combine(LogDir, $"app.log.{i}");
                    var nextArchive = Path.Combine(LogDir, $"app.log.{i + 1}");
                    
                    if (File.Exists(currentArchive))
                    {
                        File.Move(currentArchive, nextArchive, overwrite: true);
                    }
                }
                
                // Архивируем текущий файл
                var firstArchive = Path.Combine(LogDir, "app.log.1");
                File.Move(LogFile, firstArchive, overwrite: true);
            }
            catch
            {
                // Если ротация не удалась, пытаемся хотя бы очистить текущий файл
                try
                {
                    File.WriteAllText(LogFile, string.Empty);
                }
                catch
                {
                    // Игнорируем
                }
            }
        }

        public static string GetLogFilePath() => LogFile;
    }
}
