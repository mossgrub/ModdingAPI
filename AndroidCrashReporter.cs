using System;
using System.IO;
using UnityEngine;

namespace Modding
{
    internal static class AndroidCrashReporter
    {
        private const string CrashLogName = "Crash.log";

#if UNITY_ANDROID && !UNITY_EDITOR

        public static void CheckPreviousCrash()
        {
            try
            {
                CheckPreviousCrashInternal();
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "Failed to inspect previous crash: " + ex);
            }
        }

        private static void CheckPreviousCrashInternal()
        {
            string outputDirectory =
                Application.persistentDataPath;

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using (AndroidJavaClass activityManagerClass =
                   new AndroidJavaClass("android.app.ActivityManager"))
            using (AndroidJavaObject unityPlayer =
                   new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                       .GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject activityManager =
                   unityPlayer.Call<AndroidJavaObject>(
                       "getSystemService",
                       "activity"))
            {
                int sdk =
                    new AndroidJavaClass("android.os.Build$VERSION")
                        .GetStatic<int>("SDK_INT");

                if (sdk < 30)
                {
                    WriteMessage(
                        "AndroidCrashReporter",
                        "ApplicationExitInfo requires Android 11/API 30+.");

                    return;
                }

                using (AndroidJavaClass exitInfoClass =
                       new AndroidJavaClass(
                           "android.app.ApplicationExitInfo"))
                {
                    int nativeCrashReason =
                        exitInfoClass.GetStatic<int>(
                            "REASON_CRASH_NATIVE");

                    AndroidJavaObject exitList =
                        activityManager.Call<AndroidJavaObject>(
                            "getHistoricalProcessExitReasons",
                            Application.identifier,
                            0,
                            10);

                    if (exitList == null)
                    {
                        WriteMessage(
                            "AndroidCrashReporter",
                            "No ApplicationExitInfo list returned.");

                        return;
                    }

                    int count =
                        exitList.Call<int>("size");

                    for (int i = 0; i < count; i++)
                    {
                        AndroidJavaObject exitInfo =
                            exitList.Call<AndroidJavaObject>(
                                "get",
                                i);

                        if (exitInfo == null)
                            continue;

                        int reason =
                            exitInfo.Call<int>("getReason");

                        if (reason != nativeCrashReason)
                            continue;

                        long timestamp =
                            exitInfo.Call<long>("getTimestamp");

                        string description =
                            exitInfo.Call<string>(
                                "getDescription");

                        int importance =
                            exitInfo.Call<int>(
                                "getImportance");

                        long pss =
                            exitInfo.Call<long>(
                                "getPss");

                        long rss =
                            exitInfo.Call<long>(
                                "getRss");

                        string stamp =
                            DateTime.Now.ToString(
                                "yyyyMMdd_HHmmss");

                        string tombstonePath =
                            Path.Combine(
                                outputDirectory,
                                "Crash_" +
                                stamp +
                                ".tombstone.pb");

                        WriteCrashLog(
                            outputDirectory,
                            stamp,
                            reason,
                            timestamp,
                            description,
                            importance,
                            pss,
                            rss,
                            tombstonePath);

                        SaveTrace(
                            exitInfo,
                            tombstonePath);

                        Logger.APILogger.LogError(
                            "Previous native crash detected. " +
                            "Crash.log and tombstone saved.");

                        break;
                    }
                }
            }
        }

        private static void WriteCrashLog(
            string directory,
            string stamp,
            int reason,
            long timestamp,
            string description,
            int importance,
            long pss,
            long rss,
            string tombstonePath)
        {
            string logPath =
                Path.Combine(
                    directory,
                    CrashLogName);

            using (StreamWriter writer =
                   new StreamWriter(
                       logPath,
                       false))
            {
                writer.WriteLine(
                    "Detected: " +
                    DateTime.Now.ToString(
                        "yyyy-MM-dd HH:mm:ss"));

                writer.WriteLine(
                    "Crash ID: " +
                    stamp);

                writer.WriteLine(
                    "Reason: " +
                    reason);

                writer.WriteLine(
                    "Exit Timestamp: " +
                    timestamp);

                writer.WriteLine(
                    "Description: " +
                    (description ?? "<null>"));

                writer.WriteLine(
                    "Importance: " +
                    importance);

                writer.WriteLine(
                    "PSS: " +
                    pss);

                writer.WriteLine(
                    "RSS: " +
                    rss);

                writer.WriteLine(
                    "Package: " +
                    Application.identifier);

                writer.WriteLine(
                    "Unity Version: " +
                    Application.unityVersion);

                writer.WriteLine(
                    "Device: " +
                    SystemInfo.deviceModel);

                writer.WriteLine(
                    "Android: " +
                    SystemInfo.operatingSystem);

                writer.WriteLine(
                    "ABI: " +
                    SystemInfo.processorType);

                writer.WriteLine(
                    "Tombstone: " +
                    tombstonePath);
            }
        }

        private static void SaveTrace(
            AndroidJavaObject exitInfo,
            string outputPath)
        {
            AndroidJavaObject trace =
                exitInfo.Call<AndroidJavaObject>(
                    "getTraceInputStream");

            if (trace == null)
            {
                WriteMessage(
                    "AndroidCrashReporter",
                    "getTraceInputStream returned null.");

                return;
            }

            byte[] buffer =
                new byte[8192];

            try
            {
                using (FileStream output =
                       new FileStream(
                           outputPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.Read))
                {
                    while (true)
                    {
                        int read =
                            trace.Call<int>(
                                "read",
                                buffer,
                                0,
                                buffer.Length);

                        if (read <= 0)
                            break;

                        output.Write(
                            buffer,
                            0,
                            read);
                    }

                    output.Flush(true);
                }
            }
            finally
            {
                try
                {
                    trace.Call("close");
                }
                catch
                {
                }
            }
        }

        private static void WriteMessage(
            string source,
            string message)
        {
            try
            {
                string path =
                    Path.Combine(
                        Application.persistentDataPath,
                        CrashLogName);

                File.AppendAllText(
                    path,
                    "[" +
                    DateTime.Now.ToString(
                        "yyyy-MM-dd HH:mm:ss") +
                    "] [" +
                    source +
                    "] " +
                    message +
                    Environment.NewLine);
            }
            catch
            {
            }
        }

#else

        public static void CheckPreviousCrash()
        {
        }

#endif
    }
}