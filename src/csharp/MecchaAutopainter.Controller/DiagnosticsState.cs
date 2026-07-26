using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

public sealed record DiagnosticsSnapshot(
    string StartupPhase,
    string AssetValidation,
    string WebView2Runtime,
    string BridgeInjection,
    string LastCode,
    string Folder);

public static class DiagnosticsState
{
    private static readonly object Gate = new();
    private static readonly object FileGate = new();
    private static AppPaths? paths;
    private static string startupPhase = "starting";
    private static string assetValidation = "not checked";
    private static string webView2Runtime = "not initialized";
    private static string bridgeInjection = "not started";
    private static string lastCode = "";
    private static string startupLogPath = "";
    private static string lastExceptionHResult = "";
    private static string lastExceptionFileName = "";
    private static string lastExceptionMessage = "";

    public static void Initialize(AppPaths appPaths, string version)
    {
        lock (Gate)
        {
            paths = appPaths;
            paths.EnsureBaseDirectories();
            startupLogPath = Path.Combine(paths.DiagnosticsDirectory, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            startupPhase = "bootstrap";
            assetValidation = "not checked";
            webView2Runtime = "not initialized";
            bridgeInjection = "not started";
            lastCode = "";
            lastExceptionHResult = "";
            lastExceptionFileName = "";
            lastExceptionMessage = "";
        }

        WriteLine("startup", $"version={version}");
        WriteLine("startup", $"os={RuntimeInformation.OSDescription}");
        WriteLine("startup", $"framework={RuntimeInformation.FrameworkDescription}");
        WriteLine("startup", $"process_arch={RuntimeInformation.ProcessArchitecture}");
        WriteLine("startup", $"process_path={Environment.ProcessPath}");
        WriteLine("startup", $"base_directory={AppContext.BaseDirectory}");
        WriteLine("startup", $"current_directory={Environment.CurrentDirectory}");
        WriteLine("startup", $"local_app_data={Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");
        WriteLine("startup", $"runtime_directory={appPaths.RuntimeDirectory}");
        WriteLine("startup", $"bridge_state_directory={appPaths.BridgeStateDirectory}");
        WriteLine("startup", $"diagnostics_directory={appPaths.DiagnosticsDirectory}");
    }

    public static void EnsureInitialized(AppPaths appPaths, string version)
    {
        lock (Gate)
        {
            if (paths is not null)
                return;
        }
        Initialize(appPaths, version);
    }

    public static void SetStartupPhase(string value) => Set(ref startupPhase, "phase", value);
    public static void SetAssetValidation(string value) => Set(ref assetValidation, "asset", value);
    public static void SetWebView2Runtime(string value) => Set(ref webView2Runtime, "webview2", value);
    public static void SetBridgeInjection(string value) => Set(ref bridgeInjection, "bridge", value);

    public static void SetLastCode(string code, string? detail = null)
    {
        lock (Gate)
            lastCode = code;
        WriteLine("code", string.IsNullOrWhiteSpace(detail) ? code : $"{code} {detail}");
    }

    public static void RecordException(string phase, Exception exception)
    {
        SetStartupPhase(phase);
        SetLastCode("MC-APP-001", exception.Message);
        lock (Gate)
        {
            lastExceptionHResult = $"0x{exception.HResult:X8}";
            lastExceptionFileName = exception is FileNotFoundException fileNotFound ? fileNotFound.FileName ?? "" : "";
            lastExceptionMessage = exception.Message;
        }
        WriteLine("exception", FormatException(exception));
        WriteCrashJson(phase, exception);
    }

    public static DiagnosticsSnapshot Snapshot(AppPaths fallbackPaths)
    {
        lock (Gate)
        {
            var folder = paths?.DiagnosticsDirectory ?? fallbackPaths.DiagnosticsDirectory;
            return new DiagnosticsSnapshot(
                startupPhase,
                assetValidation,
                webView2Runtime,
                bridgeInjection,
                lastCode,
                folder);
        }
    }

    public static string Summary(AppPaths fallbackPaths)
    {
        var snapshot = Snapshot(fallbackPaths);
        var builder = new StringBuilder();
        builder.AppendLine("Meccha Camouflage diagnostics");
        builder.AppendLine($"startup_phase: {snapshot.StartupPhase}");
        builder.AppendLine($"asset_validation: {snapshot.AssetValidation}");
        builder.AppendLine($"webview2_runtime: {snapshot.WebView2Runtime}");
        builder.AppendLine($"bridge_injection: {snapshot.BridgeInjection}");
        builder.AppendLine($"last_code: {(snapshot.LastCode.Length == 0 ? "-" : snapshot.LastCode)}");
        if (!string.IsNullOrWhiteSpace(lastExceptionMessage))
            builder.AppendLine($"last_exception: {lastExceptionMessage}");
        if (!string.IsNullOrWhiteSpace(lastExceptionHResult))
            builder.AppendLine($"last_exception_hresult: {lastExceptionHResult}");
        if (!string.IsNullOrWhiteSpace(lastExceptionFileName))
            builder.AppendLine($"last_exception_file: {lastExceptionFileName}");
        builder.AppendLine($"diagnostics_folder: {snapshot.Folder}");
        if (!string.IsNullOrWhiteSpace(startupLogPath))
            builder.AppendLine($"startup_log: {startupLogPath}");
        return builder.ToString().TrimEnd();
    }

    public static void WriteLine(string category, string message)
    {
        string path;
        lock (Gate)
        {
            if (paths is null)
                return;
            Directory.CreateDirectory(paths.DiagnosticsDirectory);
            path = string.IsNullOrWhiteSpace(startupLogPath)
                ? Path.Combine(paths.DiagnosticsDirectory, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log")
                : startupLogPath;
        }
        TryAppendLine(path, $"{DateTimeOffset.Now:O} [{category}] {message}");
    }

    private static void Set(ref string field, string category, string value)
    {
        lock (Gate)
            field = value;
        WriteLine(category, value);
    }

    private static string FormatException(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            builder.AppendLine($"[{depth}] {current.GetType().FullName}: {current.Message}");
            builder.AppendLine($"hresult=0x{current.HResult:X8}");
            if (current is FileNotFoundException fileNotFound && !string.IsNullOrWhiteSpace(fileNotFound.FileName))
                builder.AppendLine($"file_name={fileNotFound.FileName}");
            builder.AppendLine(current.StackTrace ?? "");
            current = current.InnerException;
            ++depth;
        }
        return builder.ToString().TrimEnd();
    }

    private static void WriteCrashJson(string phase, Exception exception)
    {
        AppPaths? currentPaths;
        lock (Gate)
            currentPaths = paths;
        if (currentPaths is null)
            return;
        Directory.CreateDirectory(currentPaths.DiagnosticsDirectory);
        var path = Path.Combine(currentPaths.DiagnosticsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var payload = new
        {
            phase,
            exception = exception.GetType().FullName,
            exception.Message,
            hresult = $"0x{exception.HResult:X8}",
            fileName = exception is FileNotFoundException fileNotFound ? fileNotFound.FileName : "",
            text = exception.ToString()
        };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never replace the original startup failure.
        }
    }

    private static void TryAppendLine(string path, string line)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A diagnostic log can be temporarily locked by another thread,
            // another app instance, an editor, or security software. Keep the
            // controller alive and preserve the original error path.
        }
    }
}
