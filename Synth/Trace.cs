using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SynthPoc
{
    /// <summary>P1 阶段的轻量诊断：所有阶段打点到 %TEMP%\SynthPoc-trace.txt，硬崩溃后还能看到最后一步。</summary>
    internal static class Trace
    {
        private static readonly object Gate = new();
        private static readonly string Path =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SynthPoc-trace.txt");

        public static void Log(string msg)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Thread.CurrentThread.ManagedThreadId + "] " + msg + "\r\n",
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Reset()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
