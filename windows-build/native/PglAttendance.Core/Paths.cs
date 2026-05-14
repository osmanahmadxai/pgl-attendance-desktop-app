using System;
using System.IO;

namespace PglAttendance.Core;

public static class Paths
{
    public static string DataDir
    {
        get
        {
            var ovr = Environment.GetEnvironmentVariable("PGL_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(ovr)) return ovr;
            var programData = Environment.GetEnvironmentVariable("ProgramData")
                              ?? @"C:\ProgramData";
            return Path.Combine(programData, "PGL Attendance");
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DatabaseFile => Path.Combine(DataDir, "attendance.db");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static string SqliteConnectionString
    {
        get
        {
            EnsureDirs();
            return $"Data Source={DatabaseFile};Cache=Shared;Pooling=True";
        }
    }

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}
