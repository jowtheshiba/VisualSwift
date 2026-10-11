using System;
using System.IO;

namespace VisualSwift
{
    internal static class PrototypeLog
    {
        private static readonly object Gate = new object();
        private static readonly string LogDirectory = Path.Combine(Path.GetTempPath(), "VisualSwift");
        private static readonly string LogPath = Path.Combine(LogDirectory, "client-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
        public static event Action<string> Written = delegate { };
        public static string FilePath => LogPath;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogPath, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { Written?.Invoke(message); }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (InvalidOperationException) { }
        }
    }
}
