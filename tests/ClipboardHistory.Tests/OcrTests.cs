using System.Diagnostics;
using System.Drawing;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Tests;

public sealed class OcrTests
{
    [Fact]
    public void StartInfo_UsesArgumentListAndKorPlusEngConfiguration()
    {
        TesseractConfiguration configuration = new(
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files\Tesseract-OCR\tessdata",
            "kor+eng",
            TimeSpan.FromSeconds(30));

        ProcessStartInfo startInfo = TesseractOcrEngine.CreateOcrStartInfo(
            configuration,
            @"C:\images\sample image.png");

        Assert.Equal(configuration.ExecutablePath, startInfo.FileName);
        Assert.Equal(
            [
                @"C:\images\sample image.png", "stdout", "--tessdata-dir",
                configuration.TessdataDirectory, "-l", "kor+eng", "--oem", "1", "--psm", "11",
            ],
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task Validation_ReportsMissingExecutableAndModels()
    {
        using TemporaryDirectory temporary = new();
        TesseractOcrEngine missingExecutable = new(new TesseractConfiguration(
            Path.Combine(temporary.Path, "missing.exe"),
            temporary.Path,
            "kor+eng",
            TimeSpan.FromSeconds(1)));
        Assert.Equal("executable_missing", (await missingExecutable.ValidateAsync()).ErrorCode);

        string executable = Path.Combine(temporary.Path, "fake.exe");
        await File.WriteAllBytesAsync(executable, [0]);
        TesseractOcrEngine missingKorean = new(new TesseractConfiguration(
            executable,
            temporary.Path,
            "kor+eng",
            TimeSpan.FromSeconds(1)));
        Assert.Equal("kor_model_missing", (await missingKorean.ValidateAsync()).ErrorCode);
    }

    [Fact]
    public async Task ProcessRunner_TimesOutAndTerminatesLongRunningProcess()
    {
        ProcessStartInfo startInfo = CreatePowerShellStartInfo("Start-Sleep -Seconds 30");

        TesseractOcrEngine.ProcessExecution execution = await TesseractOcrEngine.RunProcessAsync(
            startInfo,
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        Assert.True(execution.Started);
        Assert.True(execution.TimedOut);
    }

    [Fact]
    public async Task ProcessRunner_HonorsCancellation()
    {
        ProcessStartInfo startInfo = CreatePowerShellStartInfo("Start-Sleep -Seconds 30");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TesseractOcrEngine.RunProcessAsync(
                startInfo,
                TimeSpan.FromSeconds(30),
                cancellation.Token));
    }

    [Fact]
    public async Task Worker_RecoversInterruptedPendingRecordAndPersistsSearchableOcrText()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        StoredImageArtifact artifact = await SaveImageAsync(store);
        long id = await host.Writer.InsertImageAsync(CreateRecord(artifact, OcrStatus.Pending));
        await host.Writer.UpdateOcrAsync(id, OcrStatus.Processing, null, null);
        FakeOcrEngine engine = new(
            TesseractAvailability.Available,
            new OcrExecutionResult(true, "한글 English 결과", null));
        OcrWorker worker = new(host.Repository, host.Writer, store, engine, host.Logger);
        worker.Complete();

        await worker.RunAsync(CancellationToken.None);

        ClipboardHistoryEntry result = Assert.Single(await host.Repository.SearchAsync("english"));
        Assert.Equal(id, result.Id);
        Assert.Equal(OcrStatus.Completed, result.OcrStatus);
        Assert.Equal("한글 English 결과", result.OcrText);
    }

    [Theory]
    [InlineData(false, "engine_error", "failed")]
    [InlineData(true, null, "completed")]
    public async Task Worker_PersistsSuccessAndFailureStatuses(
        bool succeeds,
        string? errorCode,
        string expectedStatus)
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        StoredImageArtifact artifact = await SaveImageAsync(store);
        long id = await host.Writer.InsertImageAsync(CreateRecord(artifact, OcrStatus.Pending));
        FakeOcrEngine engine = new(
            TesseractAvailability.Available,
            new OcrExecutionResult(succeeds, succeeds ? "recognized" : null, errorCode));
        OcrWorker worker = new(host.Repository, host.Writer, store, engine, host.Logger);
        worker.Complete();

        await worker.RunAsync(CancellationToken.None);

        (string status, string? error) = await ReadOcrStateAsync(host.DatabasePath, id);
        Assert.Equal(expectedStatus, status);
        Assert.Equal(errorCode, error);
    }

    [Fact]
    public async Task Worker_MarksPendingRecordsUnavailableWithoutLosingImages()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        StoredImageArtifact artifact = await SaveImageAsync(store);
        long id = await host.Writer.InsertImageAsync(CreateRecord(artifact, OcrStatus.Pending));
        FakeOcrEngine engine = new(
            new TesseractAvailability(false, "tesseract_missing"),
            new OcrExecutionResult(false, null, "tesseract_missing"));
        OcrWorker worker = new(host.Repository, host.Writer, store, engine, host.Logger);
        worker.Complete();

        await worker.RunAsync(CancellationToken.None);

        (string status, string? error) = await ReadOcrStateAsync(host.DatabasePath, id);
        Assert.Equal(OcrStatus.Unavailable, status);
        Assert.Equal("tesseract_missing", error);
        Assert.True(File.Exists(artifact.FullPath));
    }

    [Fact]
    public async Task Worker_ExecutesOnlyOneRecognitionAtATime()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        StoredImageArtifact artifact = await SaveImageAsync(store);
        List<long> ids = [];
        for (int index = 0; index < 3; index++)
        {
            ids.Add(await host.Writer.InsertImageAsync(CreateRecord(
                artifact,
                OcrStatus.Completed) with { CopiedAt = DateTimeOffset.UtcNow.AddSeconds(index) }));
        }

        FakeOcrEngine engine = new(
            TesseractAvailability.Available,
            new OcrExecutionResult(true, "done", null),
            TimeSpan.FromMilliseconds(50));
        OcrWorker worker = new(host.Repository, host.Writer, store, engine, host.Logger);
        foreach (long id in ids)
        {
            Assert.True(worker.TryEnqueue(new PendingOcrRecord(id, artifact.RelativePath)));
        }
        worker.Complete();

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, engine.MaximumConcurrency);
        Assert.Equal(1, worker.MaximumConcurrentRecognitions);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string command)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static async Task<StoredImageArtifact> SaveImageAsync(ClipboardImageStore store)
    {
        using Bitmap bitmap = new(12, 8);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        return await store.SaveAsync(bitmap);
    }

    private static StoredImageRecord CreateRecord(StoredImageArtifact artifact, string status) => new(
        artifact.Sha256,
        artifact.RelativePath,
        artifact.ByteLength,
        artifact.Width,
        artifact.Height,
        DateTimeOffset.UtcNow,
        status);

    private static async Task<(string Status, string? Error)> ReadOcrStateAsync(
        string databasePath,
        long id)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ocr_status, ocr_error_code FROM clipboard_history WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed class FakeOcrEngine(
        TesseractAvailability availability,
        OcrExecutionResult result,
        TimeSpan? delay = null) : IOcrEngine
    {
        private int _concurrency;
        private int _maximumConcurrency;

        public TesseractAvailability Availability { get; } = availability;

        public int MaximumConcurrency => _maximumConcurrency;

        public async Task<OcrExecutionResult> RecognizeAsync(
            string imagePath,
            CancellationToken cancellationToken = default)
        {
            int concurrency = Interlocked.Increment(ref _concurrency);
            _maximumConcurrency = Math.Max(_maximumConcurrency, concurrency);
            try
            {
                if (delay.HasValue)
                {
                    await Task.Delay(delay.Value, cancellationToken);
                }

                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }
}
