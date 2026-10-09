namespace ActivityRecordingService.Detection;

public enum CameraEventState
{
    Idle,
    Arming,
    Recording,
    CoolingDown
}

public enum EventTransitionKind
{
    Start,
    Complete,
    CompleteAndStart
}

public enum EventEndReason
{
    None,
    NoPerson,
    MaxDuration,
    StreamStopped,
    ServiceStopping,
    StartupRecovered,
    StartupIncomplete
}

public sealed record EventTransition(
    DateTimeOffset TimestampUtc,
    EventTransitionKind Kind,
    EventEndReason Reason);

public sealed record DetectionSample(
    DateTimeOffset TimestampUtc,
    bool PersonDetected,
    float Confidence);

public sealed class EventStateMachineOptions
{
    public int TriggerHitCount { get; set; } = 2;
    public int TriggerWindowSeconds { get; set; } = 5;
    public int EventCooldownSeconds { get; set; } = 30;
    public int MaxEventDurationHours { get; set; } = 2;
}

public sealed class EventStateMachine
{
    private readonly EventStateMachineOptions _options;
    private readonly Queue<DateTimeOffset> _armingHits = new();
    private DateTimeOffset? _eventStartUtc;
    private DateTimeOffset? _lastHitUtc;
    private DateTimeOffset? _lastTimestampUtc;

    public EventStateMachine(EventStateMachineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.TriggerHitCount <= 0 ||
            options.TriggerWindowSeconds <= 0 ||
            options.EventCooldownSeconds <= 0 ||
            options.MaxEventDurationHours <= 0)
        {
            throw new ArgumentException(
                "Event timing options must be positive.",
                nameof(options));
        }
    }

    public CameraEventState State { get; private set; } = CameraEventState.Idle;

    public EventTransition? Process(DetectionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ValidateTimeOrder(sample.TimestampUtc, nameof(sample));
        _lastTimestampUtc = sample.TimestampUtc;

        if (IsRecordingState)
        {
            return ProcessActiveEventSample(sample.TimestampUtc, sample.PersonDetected);
        }

        if (!sample.PersonDetected)
        {
            ExpireArmingHits(sample.TimestampUtc);
            return null;
        }

        return AddArmingHit(sample.TimestampUtc);
    }

    public EventTransition? Advance(DateTimeOffset now)
    {
        ValidateTimeOrder(now, nameof(now));
        _lastTimestampUtc = now;

        if (IsRecordingState)
        {
            var maxDuration = TimeSpan.FromHours(_options.MaxEventDurationHours);
            if (now - _eventStartUtc! >= maxDuration)
            {
                return Complete(now, EventEndReason.MaxDuration);
            }

            var cooldown = TimeSpan.FromSeconds(_options.EventCooldownSeconds);
            if (now - _lastHitUtc! >= cooldown)
            {
                return Complete(now, EventEndReason.NoPerson);
            }

            return null;
        }

        if (State == CameraEventState.Arming)
        {
            ExpireArmingHits(now);
        }

        return null;
    }

    public void Reset()
    {
        _armingHits.Clear();
        _eventStartUtc = null;
        _lastHitUtc = null;
        _lastTimestampUtc = null;
        State = CameraEventState.Idle;
    }

    private bool IsRecordingState => State is CameraEventState.Recording or CameraEventState.CoolingDown;

    private EventTransition? ProcessActiveEventSample(
        DateTimeOffset timestampUtc,
        bool personDetected)
    {
        var maxDuration = TimeSpan.FromHours(_options.MaxEventDurationHours);
        if (timestampUtc - _eventStartUtc! >= maxDuration)
        {
            if (personDetected)
            {
                StartEvent(timestampUtc);
                return new EventTransition(
                    timestampUtc,
                    EventTransitionKind.CompleteAndStart,
                    EventEndReason.MaxDuration);
            }

            return Complete(timestampUtc, EventEndReason.MaxDuration);
        }

        var cooldown = TimeSpan.FromSeconds(_options.EventCooldownSeconds);
        if (timestampUtc - _lastHitUtc! >= cooldown)
        {
            return Complete(timestampUtc, EventEndReason.NoPerson);
        }

        if (personDetected)
        {
            _lastHitUtc = timestampUtc;
            State = CameraEventState.Recording;
            return null;
        }

        State = CameraEventState.CoolingDown;
        return null;
    }

    private EventTransition? AddArmingHit(DateTimeOffset timestampUtc)
    {
        ExpireArmingHits(timestampUtc);
        _armingHits.Enqueue(timestampUtc);
        _lastHitUtc = timestampUtc;

        if (_armingHits.Count < _options.TriggerHitCount)
        {
            State = CameraEventState.Arming;
            return null;
        }

        StartEvent(timestampUtc);
        return new EventTransition(
            timestampUtc,
            EventTransitionKind.Start,
            EventEndReason.None);
    }

    private void ExpireArmingHits(DateTimeOffset timestampUtc)
    {
        var window = TimeSpan.FromSeconds(_options.TriggerWindowSeconds);
        while (_armingHits.Count > 0 && timestampUtc - _armingHits.Peek() > window)
        {
            _armingHits.Dequeue();
        }

        if (_armingHits.Count == 0 && State == CameraEventState.Arming)
        {
            State = CameraEventState.Idle;
        }
    }

    private void StartEvent(DateTimeOffset timestampUtc)
    {
        _armingHits.Clear();
        _eventStartUtc = timestampUtc;
        _lastHitUtc = timestampUtc;
        State = CameraEventState.Recording;
    }

    private EventTransition Complete(DateTimeOffset timestampUtc, EventEndReason reason)
    {
        _eventStartUtc = null;
        _lastHitUtc = null;
        State = CameraEventState.Idle;
        return new EventTransition(timestampUtc, EventTransitionKind.Complete, reason);
    }

    private void ValidateTimeOrder(DateTimeOffset timestampUtc, string parameterName)
    {
        if (_lastTimestampUtc is { } previous && timestampUtc < previous)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                timestampUtc,
                "Timestamps must not move backward.");
        }
    }
}
