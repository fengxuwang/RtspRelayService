using System.Globalization;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;

namespace ActivityRecordingService.Tests.Recording;

public sealed class CameraEventRecorderTests : IDisposable
{
    private static readonly DateTimeOffset StartedUtc =
        DateTimeOffset.Parse("2026-09-22T10:00:00.000Z");

    private readonly DirectoryInfo _rootDirectory =
        Directory.CreateTempSubdirectory("camera-event-recorder-");
    private readonly FakeTimeProvider _timeProvider = new(StartedUtc);
    private readonly RecordingLogger<CameraEventRecorder> _logger = new();

    static CameraEventRecorderTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task AddSegments_ClosesPartAtTenMinutesAndContinuesAtNextIndex()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], ActiveSegmentPath: null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        runner.EventId = eventId;

        for (var sequence = 0; sequence < 120; sequence++)
            await recorder.AddClosedSegmentAsync(
                CreateSegment(eventDirectory, sequence, TimeSpan.FromSeconds(5), bytes: 1),
                CancellationToken.None);

        runner.Requests.Should().ContainSingle().Which.Should().Match<EventPartRequest>(request =>
            request.EventId == eventId &&
            request.CameraName == "camera-a" &&
            request.PartIndex == 0 &&
            request.EventDirectory == eventDirectory &&
            request.Segments.Count == 120);
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().ContainSingle().Which.Should().Match<RecordingFileRecord>(file =>
                file.PartIndex == 0 &&
                file.RelativePath.EndsWith("part-000.mp4", StringComparison.Ordinal) &&
                file.Bytes == 8 &&
                file.Status == RecordingFileStatus.Ready);
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Recording);
        recorder.Snapshot.NextPartIndex.Should().Be(1);
        recorder.Snapshot.PendingSegmentCount.Should().Be(0);
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Recording);

        await recorder.AddClosedSegmentAsync(
            CreateSegment(eventDirectory, sequence: 120, TimeSpan.FromMinutes(10), bytes: 1),
            CancellationToken.None);

        runner.Requests.Should().HaveCount(2);
        runner.Requests[1].PartIndex.Should().Be(1);
    }

    [Fact]
    public async Task CompleteAsync_AfterShortEventWritesFirstPartAndCompletesEvent()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        var segment = CreateSegment(eventDirectory, sequence: 0, TimeSpan.FromSeconds(5), bytes: 1);
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([segment], "unused-active-path"),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        _timeProvider.Set(StartedUtc.AddSeconds(30));

        await recorder.CompleteAsync(EventEndReason.NoPerson, CancellationToken.None);

        runner.Requests.Should().ContainSingle().Which.Segments
            .Select(requestSegment => requestSegment.FilePath)
            .Should().Contain(segment.FilePath);
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Ready);
        var storedEvent = await repository.GetEventAsync(eventId, CancellationToken.None);
        storedEvent.Should().Be(new EventRecord(
            eventId,
            "camera-a",
            StartedUtc,
            StartedUtc.AddSeconds(30),
            EventStatus.Completed,
            FileBytes: 8,
            FileCount: 1,
            EventEndReason.NoPerson));
        File.Exists(segment.FilePath).Should().BeFalse();
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Completed);
    }

    [Fact]
    public async Task BeginEvent_AfterCompletionStartsANewAggregate()
    {
        var (recorder, repository, _, _) = await CreateRecorderAsync();
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var firstEventId = recorder.Snapshot.EventId!.Value;
        _timeProvider.Set(StartedUtc.AddSeconds(30));
        await recorder.CompleteAsync(EventEndReason.MaxDuration, CancellationToken.None);
        _timeProvider.Set(StartedUtc.AddMinutes(2));

        await recorder.BeginEventAsync(
            StartedUtc.AddMinutes(2),
            new EventSegmentReservation([], null),
            CancellationToken.None);

        var secondEventId = recorder.Snapshot.EventId!.Value;
        secondEventId.Should().NotBe(firstEventId);
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Recording);
        recorder.Snapshot.PendingSegmentCount.Should().Be(0);
        (await repository.GetEventAsync(secondEventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Recording);
    }

    [Fact]
    public async Task AddSegments_InsertsReadyMetadataOnlyAfterSuccessfulRename()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        IReadOnlyList<RecordingFileRecord>? filesDuringFfmpeg = null;
        var temporaryFileExistedWhenProcessExited = false;
        var finalFileExistedWhenProcessExited = false;
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        runner.EventId = eventId;
        runner.BeforeProcessExit = () =>
        {
            filesDuringFfmpeg = repository
                .GetFilesAsync(eventId, CancellationToken.None)
                .GetAwaiter().GetResult();
            temporaryFileExistedWhenProcessExited =
                File.Exists(Path.Combine(eventDirectory, "part-000.tmp.mp4"));
            finalFileExistedWhenProcessExited =
                File.Exists(Path.Combine(eventDirectory, "part-000.mp4"));
        };

        await recorder.AddClosedSegmentAsync(
            CreateSegment(eventDirectory, sequence: 0, TimeSpan.FromMinutes(10), bytes: 1),
            CancellationToken.None);

        filesDuringFfmpeg.Should().ContainSingle().Which.Should().Match<RecordingFileRecord>(file =>
            file.PartIndex == 0 &&
            file.Status == RecordingFileStatus.Reserved &&
            file.Bytes == 0);
        temporaryFileExistedWhenProcessExited.Should().BeTrue();
        finalFileExistedWhenProcessExited.Should().BeFalse();
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Ready);
    }

    [Fact]
    public async Task AddSegments_WhenDatabaseFailsAfterRenameLeavesReservedFileRecoverable()
    {
        var innerRepository = new SqliteEventRepository(
            Path.Combine(_rootDirectory.FullName, "events.db"),
            defaultRetentionDays: 7);
        await innerRepository.InitializeAsync(CancellationToken.None);
        var repository = new ReadyUpdateFailingRepository(innerRepository);
        var (recorder, _, runner, eventDirectory) = await CreateRecorderAsync(repository);
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        runner.EventId = eventId;

        var addSegment = () => recorder.AddClosedSegmentAsync(
            CreateSegment(eventDirectory, sequence: 0, TimeSpan.FromMinutes(10), bytes: 1),
            CancellationToken.None);

        await addSegment.Should().ThrowAsync<InvalidOperationException>();
        var finalPath = Path.Combine(eventDirectory, "part-000.mp4");
        File.Exists(finalPath).Should().BeTrue();
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Reserved);
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
    }

    [Fact]
    public async Task AddSegments_WhenFfmpegFails_MarksEventFailedRetainsSegmentsAndStopsAggregate()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        runner.ExitCode = 19;
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        var segment = CreateSegment(
            eventDirectory,
            sequence: 0,
            TimeSpan.FromSeconds(5),
            bytes: 536_870_912);

        var addSegment = () => recorder.AddClosedSegmentAsync(segment, CancellationToken.None);

        await addSegment.Should().ThrowAsync<InvalidOperationException>();
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Failed);
        File.Exists(segment.FilePath).Should().BeTrue();
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
        recorder.Snapshot.PendingSegmentCount.Should().Be(1);
        recorder.Snapshot.NextPartIndex.Should().Be(0);

        _timeProvider.Set(StartedUtc.AddMinutes(2));
        await recorder.BeginEventAsync(
            StartedUtc.AddMinutes(2),
            new EventSegmentReservation([], null),
            CancellationToken.None);

        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Recording);
        recorder.Snapshot.EventId.Should().NotBe(eventId);
        runner.EventId = recorder.Snapshot.EventId!.Value;
        (await repository.GetEventAsync(
            recorder.Snapshot.EventId!.Value,
            CancellationToken.None))!.Status.Should().Be(EventStatus.Recording);

        var anotherSegment = CreateSegment(
            eventDirectory,
            sequence: 1,
            TimeSpan.FromSeconds(5),
            bytes: 536_870_912);
        var retry = () => recorder.AddClosedSegmentAsync(anotherSegment, CancellationToken.None);

        await retry.Should().ThrowAsync<InvalidOperationException>();
        runner.Requests.Should().HaveCount(2);
        File.Exists(anotherSegment.FilePath).Should().BeTrue();
        _logger.Messages.Should().Contain(message =>
            message.Contains("camera-a", StringComparison.Ordinal) &&
            message.Contains(eventId.ToString(), StringComparison.Ordinal));
        _logger.Messages.Should().NotContain(message =>
            message.Contains(_rootDirectory.FullName, StringComparison.Ordinal) ||
            message.Contains(eventDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailAsync_MarksActiveEventFailedAndRetainsPendingSegments()
    {
        var (recorder, repository, _, eventDirectory) = await CreateRecorderAsync();
        var segment = CreateSegment(eventDirectory, sequence: 0, TimeSpan.FromSeconds(5), bytes: 1);
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([segment], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        _timeProvider.Set(StartedUtc.AddSeconds(5));

        await recorder.FailAsync("recording stream failed", CancellationToken.None);

        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        File.Exists(segment.FilePath).Should().BeTrue();
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
        recorder.Snapshot.PendingSegmentCount.Should().Be(1);

        _timeProvider.Set(StartedUtc.AddMinutes(2));
        await recorder.BeginEventAsync(
            StartedUtc.AddMinutes(2),
            new EventSegmentReservation([], null),
            CancellationToken.None);

        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Recording);
        recorder.Snapshot.EventId.Should().NotBe(eventId);
    }

    [Fact]
    public async Task CompleteAsync_WithoutPlayablePartMarksEventFailed()
    {
        var (recorder, repository, _, _) = await CreateRecorderAsync();
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        _timeProvider.Set(StartedUtc.AddSeconds(5));

        await recorder.CompleteAsync(EventEndReason.StreamStopped, CancellationToken.None);

        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
    }

    [Fact]
    public async Task CompleteAsync_WhenCanceled_PersistsFailedAndPropagatesCancellation()
    {
        var (recorder, repository, _, _) = await CreateRecorderAsync();
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var complete = () => recorder.CompleteAsync(
            EventEndReason.ServiceStopping,
            cancellationSource.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
    }

    [Fact]
    public async Task FailAsync_WhenCanceled_PersistsFailedAndPropagatesCancellation()
    {
        var (recorder, repository, _, _) = await CreateRecorderAsync();
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var fail = () => recorder.FailAsync("stream stopped", cancellationSource.Token);

        await fail.Should().ThrowAsync<OperationCanceledException>();
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
    }

    [Fact]
    public async Task CancellationWaitingForGate_WaitsForActiveOperationAndKeepsTerminalState()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        var segment = CreateSegment(eventDirectory, sequence: 0, TimeSpan.FromSeconds(5), bytes: 1);
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([segment], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        var completionHoldsGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BeforeProcessExit = () =>
        {
            completionHoldsGate.TrySetResult();
            releaseCompletion.Task.Wait();
        };

        var completeTask = recorder.CompleteAsync(EventEndReason.NoPerson, CancellationToken.None);
        await completionHoldsGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var canceledFailTask = recorder.FailAsync("service stopping", cancellationSource.Token);
        await Task.Delay(100);

        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Recording);

        releaseCompletion.TrySetResult();
        await completeTask;
        var canceledFail = () => canceledFailTask;
        await canceledFail.Should().ThrowAsync<OperationCanceledException>();

        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Completed);
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Completed);
    }

    [Fact]
    public async Task AddClosedSegment_WhenPartWorkflowCancels_PersistsFailedAndPropagates()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        runner.EventId = eventId;
        using var cancellationSource = new CancellationTokenSource();
        runner.CancelDuringWait = cancellationSource;
        var segment = CreateSegment(
            eventDirectory,
            sequence: 0,
            TimeSpan.FromMinutes(10),
            bytes: 1);

        var addSegment = () => recorder.AddClosedSegmentAsync(segment, cancellationSource.Token);

        await addSegment.Should().ThrowAsync<OperationCanceledException>();
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        File.Exists(segment.FilePath).Should().BeTrue();
        recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
        recorder.Snapshot.PendingSegmentCount.Should().Be(1);
    }

    [Fact]
    public async Task AddClosedSegment_WhenFfmpegFails_LogsBoundedRedactedDiagnostics()
    {
        var (recorder, repository, runner, eventDirectory) = await CreateRecorderAsync();
        runner.ExitCode = 19;
        runner.StandardErrorLines =
        [
            "failed rtsp://admin:diagnostic-secret@camera.example/main",
            $"output {eventDirectory}"
        ];
        await recorder.BeginEventAsync(
            StartedUtc,
            new EventSegmentReservation([], null),
            CancellationToken.None);
        var eventId = recorder.Snapshot.EventId!.Value;
        runner.EventId = eventId;

        var addSegment = () => recorder.AddClosedSegmentAsync(
            CreateSegment(eventDirectory, sequence: 0, TimeSpan.FromMinutes(10), bytes: 1),
            CancellationToken.None);

        await addSegment.Should().ThrowAsync<EventPartWriteException>();
        _logger.Messages.Should().Contain(message =>
            message.Contains("[redacted-url]", StringComparison.Ordinal));
        _logger.Messages.Should().NotContain(message =>
            message.Contains("diagnostic-secret", StringComparison.Ordinal) ||
            message.Contains("camera.example", StringComparison.Ordinal) ||
            message.Contains("rtsp://", StringComparison.Ordinal) ||
            message.Contains("rtsps://", StringComparison.Ordinal) ||
            message.Contains("rtmp://", StringComparison.Ordinal) ||
            message.Contains("rtmps://", StringComparison.Ordinal) ||
            message.Contains(eventDirectory, StringComparison.Ordinal) ||
            message.Contains(_rootDirectory.FullName, StringComparison.Ordinal));
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
    }

    private async Task<(CameraEventRecorder Recorder, IEventRepository Repository, RecordingProcessRunner Runner, string EventDirectory)> CreateRecorderAsync(
        IEventRepository? repositoryOverride = null)
    {
        var settings = new ActivityRecordingOptions
        {
            RecordingRoot = _rootDirectory.FullName
        };
        var camera = new ValidatedCameraOptions(
            "camera-a",
            "rtsp://camera.example/sub",
            "rtsp://camera.example/main",
            Roi: null,
            RetentionDays: null,
            Settings: new CameraOptions());
        var service = new ValidatedActivityRecordingOptions(
            "/usr/bin/ffmpeg",
            settings,
            [camera],
            Errors: []);
        var repository = repositoryOverride ?? new SqliteEventRepository(
            Path.Combine(_rootDirectory.FullName, "events.db"),
            defaultRetentionDays: 7);
        if (repositoryOverride is null)
            await repository.InitializeAsync(CancellationToken.None);
        var runner = new RecordingProcessRunner();
        var segmentManager = new RollingSegmentManager(
            _timeProvider,
            settings.Recording);
        var recorder = new CameraEventRecorder(
            camera,
            service,
            repository,
            segmentManager,
            new EventPartWriter("/usr/bin/ffmpeg", runner),
            _timeProvider,
            _logger);
        var eventDirectory = Path.Combine(
            _rootDirectory.FullName,
            "events",
            "camera-a",
            "pending-event");
        Directory.CreateDirectory(eventDirectory);
        return (recorder, repository, runner, eventDirectory);
    }

    private static RollingSegment CreateSegment(
        string eventDirectory,
        int sequence,
        TimeSpan duration,
        long bytes)
    {
        var path = Path.Combine(eventDirectory, $"rolling_{sequence:D9}.mp4");
        File.WriteAllBytes(path, bytes > 1_024 ? [1] : new byte[bytes]);
        return new RollingSegment(path, bytes, duration);
    }

    public void Dispose() => _rootDirectory.Delete(recursive: true);

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private byte[] _outputBytes = [1, 2, 3, 4, 5, 6, 7, 8];

        public List<EventPartRequest> Requests { get; } = [];

        public Action? BeforeProcessExit { get; set; }

        public Guid EventId { get; set; }

        public int ExitCode { get; set; }

        public CancellationTokenSource? CancelDuringWait { get; set; }

        public IReadOnlyList<string> StandardErrorLines { get; set; } = [];

        public Task<IManagedProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = command.Arguments[Array.IndexOf(command.Arguments.ToArray(), "-i") + 1];
            var eventDirectory = Path.GetDirectoryName(manifestPath)!;
            var segments = File.ReadAllLines(manifestPath)
                .Where(line => line.StartsWith("file '", StringComparison.Ordinal))
                .Select(EscapeConcatPath)
                .ToArray();
            foreach (var line in StandardErrorLines)
                onStandardError(line);
            Requests.Add(new EventPartRequest(
                EventId,
                "camera-a",
                ParsePartIndex(manifestPath),
                eventDirectory,
                segments));
            File.WriteAllBytes(command.Arguments[^1], _outputBytes);

            return Task.FromResult<IManagedProcess>(new RecordingManagedProcess(
                ExitCode,
                BeforeProcessExit,
                CancelDuringWait));
        }

        private static RollingSegment ParseSegment(string path, long bytes) =>
            new(path, bytes, TimeSpan.FromSeconds(5));

        private static int ParsePartIndex(string manifestPath) =>
            int.Parse(
                Path.GetFileName(manifestPath)["part-".Length..].Split('.')[0],
                CultureInfo.InvariantCulture);

        private static RollingSegment EscapeConcatPath(string line) =>
            ParseSegment(line[6..^1].Replace("'\\''", "'", StringComparison.Ordinal), bytes: 8);
    }

    private sealed class ReadyUpdateFailingRepository(IEventRepository inner) : IEventRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public Task UpsertCameraAsync(
            string cameraName,
            bool enabled,
            int? retentionDays,
            CancellationToken cancellationToken) =>
            inner.UpsertCameraAsync(cameraName, enabled, retentionDays, cancellationToken);

        public Task<Guid> CreateEventAsync(
            string cameraName,
            DateTimeOffset startedUtc,
            CancellationToken cancellationToken) =>
            inner.CreateEventAsync(cameraName, startedUtc, cancellationToken);

        public Task CompleteEventAsync(
            Guid eventId,
            DateTimeOffset endedUtc,
            EventStatus status,
            CancellationToken cancellationToken,
            EventEndReason? endReason = null) =>
            inner.CompleteEventAsync(eventId, endedUtc, status, cancellationToken, endReason);

        public Task AddFileAsync(
            RecordingFileRecord file,
            CancellationToken cancellationToken) =>
            inner.AddFileAsync(file, cancellationToken);

        public Task SetFileStatusAsync(
            Guid fileId,
            RecordingFileStatus status,
            CancellationToken cancellationToken) =>
            inner.SetFileStatusAsync(fileId, status, cancellationToken);

        public Task SetFileReadyAsync(
            Guid fileId,
            long bytes,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database fault after rename");

        public Task<EventRecord?> GetEventAsync(Guid eventId, CancellationToken cancellationToken) =>
            inner.GetEventAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
            Guid eventId,
            CancellationToken cancellationToken) =>
            inner.GetFilesAsync(eventId, cancellationToken);

        public Task<RecordingFileRecord?> GetReadyFileAsync(
            Guid fileId,
            CancellationToken cancellationToken) =>
            inner.GetReadyFileAsync(fileId, cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListEventsAsync(
            string? cameraName,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListEventsAsync(cameraName, fromUtc, toUtc, offset, limit, cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
            EventStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListEventsByStatusAsync(status, limit, cancellationToken);

        public Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
            string? cameraName,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListFileEventsAsync(cameraName, offset, limit, cancellationToken);

        public Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken) =>
            inner.GetRecordingBytesAsync(cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListDeletableEventsAsync(nowUtc, limit, cancellationToken);

        public Task AddCleanupAuditAsync(
            string reason,
            int eventCount,
            long freedBytes,
            CancellationToken cancellationToken) =>
            inner.AddCleanupAuditAsync(reason, eventCount, freedBytes, cancellationToken);

        public Task DeleteEventAsync(Guid eventId, CancellationToken cancellationToken) =>
            inner.DeleteEventAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
            RecordingFileStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListFilesByStatusAsync(status, limit, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
        }
    }

    private sealed class RecordingManagedProcess(
        int exitCode,
        Action? beforeExit,
        CancellationTokenSource? cancelDuringWait) : IManagedProcess
    {
        public int Id => 23456;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            beforeExit?.Invoke();
            if (cancelDuringWait is not null)
            {
                cancelDuringWait.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(exitCode);
        }

        public Task KillTreeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Set(DateTimeOffset value) => _now = value;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Messages.Add(formatter(state, exception));
        }
    }
}
