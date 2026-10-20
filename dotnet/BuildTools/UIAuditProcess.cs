using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

// No cmd.exe / shell expansion. Windows CRT command-line quoting.
public sealed class BenchProcess : IDisposable
{
    private Process process;
    private Task<string> stdout, stderr;
    private Stopwatch clock;
    public static string Quote(string value)
    {
        if (value == null) value = "";
        StringBuilder b = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); }
            else { b.Append('\\', slashes); b.Append(c); }
            slashes = 0;
        }
        b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
    }
    public static string Join(string[] args)
    {
        StringBuilder b = new StringBuilder();
        foreach (string arg in args) { if (b.Length > 0) b.Append(' '); b.Append(Quote(arg)); }
        return b.ToString();
    }
    public void Start(string exe, string[] args, string directory)
    {
        var si = new ProcessStartInfo(exe, Join(args));
        si.WorkingDirectory = directory; si.UseShellExecute = false;
        si.CreateNoWindow = true; si.RedirectStandardOutput = true; si.RedirectStandardError = true;
        process = new Process(); process.StartInfo = si; clock = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("Process did not start");
        stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
    }
    public bool Done { get { return process.HasExited; } }
    public double Seconds { get { return clock.Elapsed.TotalSeconds; } }
    public int Finish() { process.WaitForExit(); clock.Stop(); Task.WaitAll(stdout, stderr); return process.ExitCode; }
    public string Output { get { return stdout.Result; } }
    public string Error { get { return stderr.Result; } }
    public void Dispose()
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } } catch { }
        process.Dispose(); process = null;
    }
}
