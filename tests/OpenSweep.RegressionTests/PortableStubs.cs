using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenSweep.Core;

// Test-only adapters for the Windows-dependent Common.cs. No algorithm under test is replaced.
public static class AppPaths
{
    public static string DataDir { get; set; }
    public static string SettingsFile => Path.Combine(DataDir, "settings.xml");
}

public static class Log
{
    public static ConcurrentQueue<string> Entries { get; } = new();
    public static void Write(string message) => Entries.Enqueue(message);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public static class Fmt
{
    // Display formatting is outside this suite's scope; do not use it as a test oracle.
    public static string Bytes(long value) => value + " bytes";
    public static string Count(long value, string singular, string plural = null) => value + " " + (value == 1 ? singular : plural ?? singular + "s");
}
