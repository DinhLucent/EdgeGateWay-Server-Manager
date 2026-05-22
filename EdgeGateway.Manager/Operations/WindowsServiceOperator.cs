using System.Diagnostics;

namespace EdgeGateway.Manager.Operations;

internal sealed class WindowsServiceOperator(string serviceName)
{
    public async Task<WindowsServiceSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsServiceSnapshot(
                serviceName,
                IsInstalled: false,
                "Unsupported",
                "",
                "",
                "Windows Service control is available only on Windows."
            );
        }

        ProcessResult query = await RunCapturedAsync(
            "sc.exe",
            $"query {QuoteArgument(serviceName)}",
            cancellationToken
        ).ConfigureAwait(false);
        if (query.ExitCode != 0)
        {
            return new WindowsServiceSnapshot(
                serviceName,
                IsInstalled: false,
                "Not installed",
                "",
                "",
                NormalizeMessage(query)
            );
        }

        ProcessResult config = await RunCapturedAsync(
            "sc.exe",
            $"qc {QuoteArgument(serviceName)}",
            cancellationToken
        ).ConfigureAwait(false);

        return new WindowsServiceSnapshot(
            serviceName,
            IsInstalled: true,
            ParseScValue(query.Output, "STATE"),
            ParseScValue(config.Output, "START_TYPE"),
            ParseScValue(config.Output, "BINARY_PATH_NAME"),
            "Installed"
        );
    }

    public Task<string> StartAsync()
    {
        return RunElevatedPowerShellAsync(
            "-NoProfile -Command "
            + QuoteArgument($"Start-Service -Name {QuotePowerShellString(serviceName)}")
        );
    }

    public Task<string> StopAsync()
    {
        return RunElevatedPowerShellAsync(
            "-NoProfile -Command "
            + QuoteArgument($"Stop-Service -Name {QuotePowerShellString(serviceName)}")
        );
    }

    public Task<string> RestartAsync()
    {
        return RunElevatedPowerShellAsync(
            "-NoProfile -Command "
            + QuoteArgument($"Restart-Service -Name {QuotePowerShellString(serviceName)}")
        );
    }

    private static async Task<ProcessResult> RunCapturedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken
    )
    {
        ProcessStartInfo startInfo = new(fileName, arguments)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static Task<string> RunElevatedPowerShellAsync(string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult("Windows Service control is available only on Windows.");
        }

        ProcessStartInfo startInfo = new("powershell.exe", arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
        };
        Process.Start(startInfo);
        return Task.FromResult("Command launched. Approve the Windows elevation prompt, then refresh status.");
    }

    private static string ParseScValue(string output, string key)
    {
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = trimmed.IndexOf(':', StringComparison.Ordinal);
            return separator >= 0
                ? trimmed[(separator + 1)..].Trim()
                : trimmed;
        }

        return "Unknown";
    }

    private static string NormalizeMessage(ProcessResult result)
    {
        string message = string.IsNullOrWhiteSpace(result.Error)
            ? result.Output
            : result.Error;
        return string.IsNullOrWhiteSpace(message)
            ? $"sc.exe exited with code {result.ExitCode}."
            : message.Trim();
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuotePowerShellString(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
