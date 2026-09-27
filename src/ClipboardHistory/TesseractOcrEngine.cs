using System.Diagnostics;
using System.Text;

namespace ClipboardHistory;

internal sealed class TesseractOcrEngine : IOcrEngine
{
    private readonly TesseractConfiguration _configuration;

    public TesseractOcrEngine(TesseractConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public TesseractAvailability Availability { get; private set; } =
        new(false, "not_validated");

    public async Task<TesseractAvailability> ValidateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configuration.ExecutablePath))
        {
            return Availability = new(false, "executable_missing");
        }

        if (!Directory.Exists(_configuration.TessdataDirectory))
        {
            return Availability = new(false, "tessdata_missing");
        }

        if (!File.Exists(Path.Combine(_configuration.TessdataDirectory, "kor.traineddata")))
        {
            return Availability = new(false, "kor_model_missing");
        }

        if (!File.Exists(Path.Combine(_configuration.TessdataDirectory, "eng.traineddata")))
        {
            return Availability = new(false, "eng_model_missing");
        }

        ProcessStartInfo startInfo = CreateLanguageValidationStartInfo(_configuration);
        ProcessExecution execution = await RunProcessAsync(
            startInfo,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (!execution.Started)
        {
            return Availability = new(false, "start_failed");
        }

        if (execution.TimedOut)
        {
            return Availability = new(false, "validation_timeout");
        }

        if (execution.ExitCode != 0)
        {
            return Availability = new(false, "validation_failed");
        }

        HashSet<string> languages = execution.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Availability = languages.Contains("kor") && languages.Contains("eng")
            ? TesseractAvailability.Available
            : new TesseractAvailability(false, "languages_missing");
    }

    public async Task<OcrExecutionResult> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (!Availability.IsAvailable)
        {
            return new(false, null, Availability.ErrorCode ?? "unavailable");
        }

        ProcessStartInfo startInfo = CreateOcrStartInfo(_configuration, imagePath);
        ProcessExecution execution = await RunProcessAsync(
            startInfo,
            _configuration.Timeout,
            cancellationToken).ConfigureAwait(false);

        if (execution.TimedOut)
        {
            return new(false, null, "timeout");
        }

        if (!execution.Started)
        {
            return new(false, null, "start_failed");
        }

        if (execution.ExitCode != 0)
        {
            return new(false, null, $"exit_{execution.ExitCode}");
        }

        return new(true, execution.StandardOutput.Trim(), null);
    }

    internal static ProcessStartInfo CreateOcrStartInfo(
        TesseractConfiguration configuration,
        string imagePath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        ProcessStartInfo startInfo = CreateBaseStartInfo(configuration.ExecutablePath);
        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("--tessdata-dir");
        startInfo.ArgumentList.Add(configuration.TessdataDirectory);
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(configuration.Languages);
        startInfo.ArgumentList.Add("--oem");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add("11");
        return startInfo;
    }

    internal static ProcessStartInfo CreateLanguageValidationStartInfo(
        TesseractConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ProcessStartInfo startInfo = CreateBaseStartInfo(configuration.ExecutablePath);
        startInfo.ArgumentList.Add("--tessdata-dir");
        startInfo.ArgumentList.Add(configuration.TessdataDirectory);
        startInfo.ArgumentList.Add("--list-langs");
        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    internal static async Task<ProcessExecution> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new(false, false, -1, string.Empty);
            }
        }
        catch
        {
            return new(false, false, -1, string.Empty);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutCancellation = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new(true, false, process.ExitCode, stdout.Result);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested &&
                                                  !cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await ObserveOutputTasksAsync(stdout, stderr).ConfigureAwait(false);
            return new(true, true, -1, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await ObserveOutputTasksAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(2000);
            }
        }
        catch
        {
            // The process may have exited between the state check and termination.
        }
    }

    private static async Task ObserveOutputTasksAsync(params Task<string>[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Output is deliberately discarded, including stderr that may contain file paths.
        }
    }

    internal sealed record ProcessExecution(
        bool Started,
        bool TimedOut,
        int ExitCode,
        string StandardOutput);
}
