using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Modding
{
    internal static class AndroidCrashReporter
    {
        private static int _installed;

        private static readonly object FileLock =
            new object();

        private static FileStream _stream;

        private static StreamWriter _writer;

        private static string _crashLogPath;

        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            Initialize();
        }

        internal static void Initialize()
        {
            if (Interlocked.Exchange(
                    ref _installed,
                    1) != 0)
            {
                return;
            }

            try
            {
                string directory =
                    Application.persistentDataPath;

                Directory.CreateDirectory(
                    directory);

                _crashLogPath =
                    Path.Combine(
                        directory,
                        "Crash.log");

                OpenManagedLog();

                Write(
                    "AndroidCrashReporter initialized");

                Write(
                    "Persistent path: " +
                    Application.persistentDataPath);

                bool nativeInstalled =
                    NativeBridge.InstallCrashHandler(
                        _crashLogPath);

                Write(
                    "Native crash handler installed: " +
                    nativeInstalled);

                Application
                    .logMessageReceivedThreaded
                    += HandleUnityLog;

                AppDomain.CurrentDomain
                    .UnhandledException
                    += HandleUnhandledException;

                Write(
                    "Crash reporter ready.");
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(
                            Application.persistentDataPath,
                            "Crash.log"),
                        "\nCrashReporter initialization failure\n" +
                        ex +
                        "\n");
                }
                catch
                {
                }
            }
        }

        internal static void Install()
        {
            Initialize();
        }

        private static void OpenManagedLog()
        {
            lock (FileLock)
            {
                if (_writer != null)
                    return;

                _stream =
                    new FileStream(
                        _crashLogPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite);

                _writer =
                    new StreamWriter(
                        _stream,
                        new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
            }
        }

        private static void HandleUnityLog(
            string message,
            string stackTrace,
            LogType type)
        {
            if (type != LogType.Error &&
                type != LogType.Exception &&
                type != LogType.Assert &&
                type != LogType.Warning)
            {
                return;
            }

            Write(
                "[" +
                type +
                "] " +
                message);

            if (!string.IsNullOrEmpty(
                    stackTrace))
            {
                Write(
                    stackTrace);
            }
        }

        private static void HandleUnhandledException(
            object sender,
            UnhandledExceptionEventArgs args)
        {
            Write(
                "[UNHANDLED EXCEPTION]");

            if (args.ExceptionObject != null)
            {
                Write(
                    args.ExceptionObject.ToString());
            }

            Write(
                "IsTerminating: " +
                args.IsTerminating);
        }

        internal static void Write(
            string message)
        {
            try
            {
                lock (FileLock)
                {
                    if (_writer == null)
                        return;

                    _writer.WriteLine(
                        "[" +
                        DateTime.UtcNow.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff") +
                        "] " +
                        message);

                    _writer.Flush();

                    try
                    {
                        _stream.Flush(true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        internal static void SetContext(
            string context)
        {
            NativeBridge.SetCrashContext(
                context);
        }
    }
}