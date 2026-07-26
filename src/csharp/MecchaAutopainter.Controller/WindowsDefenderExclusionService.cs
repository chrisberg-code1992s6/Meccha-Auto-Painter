using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MecchaCamouflage.Controller;

public enum WindowsDefenderExclusionCheck
{
    Configured,
    Missing,
    Hidden,
    Unavailable
}

public enum WindowsDefenderExclusionOutcome
{
    AlreadyConfigured,
    Added,
    Unavailable,
    Cancelled,
    AddFailed
}

public sealed record WindowsDefenderExclusionResult(
    WindowsDefenderExclusionOutcome Outcome,
    string Message);

public interface IWindowsDefenderExclusionPlatform
{
    Task<WindowsDefenderExclusionCheck> CheckAsync(
        string path,
        CancellationToken cancellationToken);

    Task<int> AddElevatedAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed class WindowsDefenderExclusionService(
    IWindowsDefenderExclusionPlatform platform)
{
    private const int ElevationCancelledExitCode = 1223;
    private const string MarkerFileName = "defender-exclusion-added.txt";

    public async Task<WindowsDefenderExclusionResult> EnsureConfiguredAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.GetFullPath(path);
        var hasConfiguredMarker = HasConfiguredMarker(normalizedPath);
        var check = await platform.CheckAsync(normalizedPath, cancellationToken);
        if (check == WindowsDefenderExclusionCheck.Configured)
        {
            WriteConfiguredMarker(normalizedPath);
            return new WindowsDefenderExclusionResult(
                WindowsDefenderExclusionOutcome.AlreadyConfigured,
                "Microsoft Defender exclusion is already configured.");
        }
        if (check == WindowsDefenderExclusionCheck.Hidden && hasConfiguredMarker)
        {
            return new WindowsDefenderExclusionResult(
                WindowsDefenderExclusionOutcome.AlreadyConfigured,
                "Microsoft Defender exclusion was previously configured.");
        }
        if (check == WindowsDefenderExclusionCheck.Unavailable)
        {
            if (hasConfiguredMarker)
            {
                return new WindowsDefenderExclusionResult(
                    WindowsDefenderExclusionOutcome.AlreadyConfigured,
                    "Microsoft Defender exclusion was previously configured.");
            }
            return new WindowsDefenderExclusionResult(
                WindowsDefenderExclusionOutcome.Unavailable,
                "Microsoft Defender exclusion settings are unavailable.");
        }

        var exitCode = await platform.AddElevatedAsync(normalizedPath, cancellationToken);
        if (exitCode == ElevationCancelledExitCode)
        {
            return new WindowsDefenderExclusionResult(
                WindowsDefenderExclusionOutcome.Cancelled,
                "Microsoft Defender exclusion setup was cancelled.");
        }
        if (exitCode != 0)
        {
            return new WindowsDefenderExclusionResult(
                WindowsDefenderExclusionOutcome.AddFailed,
                $"Microsoft Defender exclusion was not added (exit code {exitCode}).");
        }

        WriteConfiguredMarker(normalizedPath);
        return new WindowsDefenderExclusionResult(
            WindowsDefenderExclusionOutcome.Added,
            "Microsoft Defender exclusion was added.");
    }

    private static bool HasConfiguredMarker(string rootDirectory)
    {
        try
        {
            return string.Equals(
                File.ReadAllText(Path.Combine(rootDirectory, MarkerFileName)).Trim(),
                "1",
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteConfiguredMarker(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        File.WriteAllText(
            Path.Combine(rootDirectory, MarkerFileName),
            "1" + Environment.NewLine);
    }
}

public sealed class PowerShellWindowsDefenderExclusionPlatform :
    IWindowsDefenderExclusionPlatform
{
    private const int MissingExitCode = 2;
    private const int HiddenExitCode = 3;
    private const int ElevationCancelledExitCode = 1223;

    public async Task<WindowsDefenderExclusionCheck> CheckAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return WindowsDefenderExclusionCheck.Unavailable;

        try
        {
            var exitCode = await RunPowerShellAsync(
                BuildCheckScript(path),
                elevated: false,
                cancellationToken);
            return exitCode switch
            {
                0 => WindowsDefenderExclusionCheck.Configured,
                MissingExitCode => WindowsDefenderExclusionCheck.Missing,
                HiddenExitCode => WindowsDefenderExclusionCheck.Hidden,
                _ => WindowsDefenderExclusionCheck.Unavailable
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return WindowsDefenderExclusionCheck.Unavailable;
        }
    }

    public async Task<int> AddElevatedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return -1;

        try
        {
            return await RunPowerShellAsync(
                BuildAddScript(path),
                elevated: true,
                cancellationToken);
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == ElevationCancelledExitCode)
        {
            return ElevationCancelledExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<int> RunPowerShellAsync(
        string script,
        bool elevated,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (elevated)
            startInfo.Verb = "runas";
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Microsoft Defender configuration process did not start.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static string BuildCheckScript(string path)
    {
        var literal = ToPowerShellLiteral(path);
        return
            "$target = [IO.Path]::GetFullPath('" + literal + "').TrimEnd('\\'); " +
            "$preference = Get-MpPreference -ErrorAction Stop; " +
            "$configured = @($preference.ExclusionPath); " +
            "foreach ($candidate in $configured) { " +
            "if ([string]::IsNullOrWhiteSpace($candidate)) { continue }; " +
            "try { $normalized = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($candidate)).TrimEnd('\\') } catch { continue }; " +
            "if ([StringComparer]::OrdinalIgnoreCase.Equals($target, $normalized)) { exit 0 } " +
            "}; " +
            "if ($preference.HideExclusionsFromLocalUsers -eq $true -or " +
            "$preference.HideExclusionsFromLocalAdmins -eq $true) { exit 3 }; " +
            "exit 2";
    }

    private static string BuildAddScript(string path)
    {
        var literal = ToPowerShellLiteral(path);
        return
            "Add-MpPreference -ExclusionPath '" + literal +
            "' -Force -ErrorAction Stop; exit 0";
    }

    private static string ToPowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
