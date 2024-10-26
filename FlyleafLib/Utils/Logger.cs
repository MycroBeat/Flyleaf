﻿using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FlyleafLib;

public static class Logger
{
    public static bool CanError => Engine.Config.LogLevel >= LogLevel.Error;
    public static bool CanWarn  => Engine.Config.LogLevel >= LogLevel.Warn;
    public static bool CanInfo  => Engine.Config.LogLevel >= LogLevel.Info;
    public static bool CanDebug => Engine.Config.LogLevel >= LogLevel.Debug;
    public static bool CanTrace => Engine.Config.LogLevel >= LogLevel.Trace;


    public static Action<string>
                        CustomOutput = DevNullPtr;
    internal static Action<string>
                        Output = DevNullPtr;
    static string       lastOutput = "";

    static ConcurrentQueue<byte[]>
                        fileData = new();
    static bool         fileTaskRunning;
    static FileStream   fileStream;
    static object       lockFileStream = new();
    static Dictionary<LogLevel, string>
                        logLevels = new();

    static Logger()
    {
        foreach (LogLevel loglevel in Enum.GetValues(typeof(LogLevel)))
            logLevels.Add(loglevel, loglevel.ToString().PadRight(5, ' '));

        // Flush File Data on Application Exit
        System.Windows.Application.Current.Exit += (o, e) =>
        {
            lock (lockFileStream)
            {
                if (fileStream != null)
                {
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);
                    fileStream.Dispose();
                }
            }
        };
    }

    internal static void SetOutput()
    {
        string output = Engine.Config.LogOutput;

        if (string.IsNullOrEmpty(output))
        {
            if (lastOutput != "")
            {
                Output = DevNullPtr;
                lastOutput = "";
            }
        }
        else if (output.StartsWith(":"))
        {
            if (output == ":console")
            {
                if (lastOutput != ":console")
                {
                    Output = Console.WriteLine;
                    lastOutput = ":console";
                }
            }
            else if (output == ":debug")
            {
                if (lastOutput != ":debug")
                {
                    Output = DebugPtr;
                    lastOutput = ":debug";
                }
            }
            else if (output == ":custom")
            {
                if (lastOutput != ":custom")
                {
                    Output = CustomOutput;
                    lastOutput = ":custom";
                }
            }
            else
                throw new Exception("Invalid log output");
        }
        else
        {
            lock (lockFileStream)
            {
                // Flush File Data on Previously Opened File Stream
                if (fileStream != null)
                {
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);
                    fileStream.Dispose();
                }

                string dir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                fileStream = new FileStream(output, Engine.Config.LogAppend ? FileMode.Append : FileMode.Create, FileAccess.Write);
                if (lastOutput != ":file")
                {
                    Output = FilePtr;
                    lastOutput = ":file";
                }
            }
        }
    }
    static void DebugPtr(string msg) => System.Diagnostics.Debug.WriteLine(msg);
    static void DevNullPtr(string msg) { }
    static void FilePtr(string msg)
    {
        fileData.Enqueue(Encoding.UTF8.GetBytes($"{msg}\r\n"));

        if (!fileTaskRunning && fileData.Count > Engine.Config.LogCachedLines)
            FlushFileData();
    }
    
    static void FlushFileData()
    {
        fileTaskRunning = true;

        Task.Run(() =>
        {
            lock (lockFileStream)
            {
                while (fileData.TryDequeue(out byte[] data))
                    fileStream.Write(data, 0, data.Length);

                fileStream.Flush();
            }

            fileTaskRunning = false;
        });
    }

    /// <summary>
    /// Forces cached file data to be written to the file
    /// </summary>
    public static void ForceFlush()
    {
        if (!fileTaskRunning && fileStream != null)
            FlushFileData();
    }

    internal static void Log(string msg, LogLevel logLevel)
    {
        if (logLevel <= Engine.Config.LogLevel)
            Output($"{DateTime.Now.ToString(Engine.Config.LogDateTimeFormat)} | {logLevels[logLevel]} | {msg}");     
    }
}

public class LogHandler
{
    public string Prefix;

    public LogHandler(string prefix = "")
        => Prefix = prefix;

    public void Error(string msg)   => Logger.Log($"{Prefix}{msg}", LogLevel.Error);
    public void Info(string msg)    => Logger.Log($"{Prefix}{msg}", LogLevel.Info);
    public void Warn(string msg)    => Logger.Log($"{Prefix}{msg}", LogLevel.Warn);
    public void Debug(string msg)   => Logger.Log($"{Prefix}{msg}", LogLevel.Debug);
    public void Trace(string msg)   => Logger.Log($"{Prefix}{msg}", LogLevel.Trace);
}

public enum LogLevel
{
    Quiet   = 0x00,
    Error   = 0x10,
    Warn    = 0x20,
    Info    = 0x30,
    Debug   = 0x40,
    Trace   = 0x50
}
