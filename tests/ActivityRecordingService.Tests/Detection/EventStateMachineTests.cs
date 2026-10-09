using ActivityRecordingService.Detection;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Detection;

public sealed class EventStateMachineTests
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-09-29T01:00:00.000Z");

    [Fact]
    public void Process_FirstHit_EntersArming()
    {
        var machine = CreateMachine();

        var transition = machine.Process(Hit(BaseTime));

        transition.Should().BeNull();
        machine.State.Should().Be(CameraEventState.Arming);
    }

    [Fact]
    public void Process_SecondHitWithinFiveSeconds_StartsEvent()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime));

        var transition = machine.Process(Hit(BaseTime.AddSeconds(5)));

        transition.Should().Be(new EventTransition(
            BaseTime.AddSeconds(5),
            EventTransitionKind.Start,
            EventEndReason.None));
        machine.State.Should().Be(CameraEventState.Recording);
    }

    [Fact]
    public void Process_MissAfterThirtySecondCooldown_CompletesEvent()
    {
        var machine = CreateMachine();
        StartEvent(machine);

        var transition = machine.Process(Miss(BaseTime.AddSeconds(30)));

        transition.Should().Be(new EventTransition(
            BaseTime.AddSeconds(30),
            EventTransitionKind.Complete,
            EventEndReason.NoPerson));
        machine.State.Should().Be(CameraEventState.Idle);
    }

    [Fact]
    public void Process_HitAtTwoHours_CompletesAndStarts()
    {
        var machine = CreateMachine();
        StartEvent(machine);

        var transition = machine.Process(Hit(BaseTime.AddHours(2)));

        transition.Should().Be(new EventTransition(
            BaseTime.AddHours(2),
            EventTransitionKind.CompleteAndStart,
            EventEndReason.MaxDuration));
        machine.State.Should().Be(CameraEventState.Recording);
    }

    [Fact]
    public void Advance_StreamStopAfterTwoHours_Completes()
    {
        var machine = CreateMachine();
        StartEvent(machine);

        var transition = machine.Advance(BaseTime.AddHours(2));

        transition.Should().Be(new EventTransition(
            BaseTime.AddHours(2),
            EventTransitionKind.Complete,
            EventEndReason.MaxDuration));
        machine.State.Should().Be(CameraEventState.Idle);
    }

    [Fact]
    public void Reset_ReturnsToIdleWithoutEvent()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime));

        machine.Reset();

        machine.State.Should().Be(CameraEventState.Idle);
        machine.Advance(BaseTime).Should().BeNull();
    }

    [Fact]
    public void Process_SecondHitOutsideFiveSeconds_DoesNotStartEvent()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime));

        var transition = machine.Process(Hit(BaseTime.AddSeconds(5.0000001)));

        transition.Should().BeNull();
        machine.State.Should().Be(CameraEventState.Arming);
    }

    [Fact]
    public void Advance_AtFiveSecondBoundary_KeepsArmingSequence()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime));

        var transition = machine.Advance(BaseTime.AddSeconds(5));

        transition.Should().BeNull();
        machine.State.Should().Be(CameraEventState.Arming);
    }

    [Fact]
    public void Process_MissBeforeCooldown_EntersCoolingDown()
    {
        var machine = CreateMachine();
        StartEvent(machine);

        var transition = machine.Process(Miss(BaseTime.AddSeconds(29.999)));

        transition.Should().BeNull();
        machine.State.Should().Be(CameraEventState.CoolingDown);
    }

    [Fact]
    public void Process_HitDuringCooldown_ContinuesEvent()
    {
        var machine = CreateMachine();
        StartEvent(machine);
        machine.Process(Miss(BaseTime.AddSeconds(1)));

        var transition = machine.Process(Hit(BaseTime.AddSeconds(2)));

        transition.Should().BeNull();
        machine.State.Should().Be(CameraEventState.Recording);
    }

    [Fact]
    public void Advance_AtThirtySecondBoundary_CompletesCooldown()
    {
        var machine = CreateMachine();
        StartEvent(machine);
        machine.Process(Miss(BaseTime.AddSeconds(1)));

        var transition = machine.Advance(BaseTime.AddSeconds(30));

        transition.Should().Be(new EventTransition(
            BaseTime.AddSeconds(30),
            EventTransitionKind.Complete,
            EventEndReason.NoPerson));
        machine.State.Should().Be(CameraEventState.Idle);
    }

    [Fact]
    public void Advance_MaxDurationTakesPriorityOverCooldown()
    {
        var machine = CreateMachine();
        StartEvent(machine);

        var transition = machine.Advance(BaseTime.AddHours(2).AddSeconds(30));

        transition.Should().Be(new EventTransition(
            BaseTime.AddHours(2).AddSeconds(30),
            EventTransitionKind.Complete,
            EventEndReason.MaxDuration));
        machine.State.Should().Be(CameraEventState.Idle);
    }

    [Fact]
    public void Process_RejectsEarlierSample()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime));

        var process = () => machine.Process(Hit(BaseTime.AddTicks(-1)));

        process.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Advance_RejectsEarlierTime()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime));

        var advance = () => machine.Advance(BaseTime.AddTicks(-1));

        advance.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reset_AllowsFreshTimelineWithoutEvent()
    {
        var machine = CreateMachine();
        machine.Process(Hit(BaseTime.AddMinutes(1)));

        machine.Reset();

        var transition = machine.Process(Hit(BaseTime));
        transition.Should().BeNull();
        machine.State.Should().Be(CameraEventState.Arming);
    }

    [Theory]
    [InlineData(0, 5, 30, 2)]
    [InlineData(2, 0, 30, 2)]
    [InlineData(2, 5, 0, 2)]
    [InlineData(2, 5, 30, 0)]
    public void Constructor_RejectsNonPositiveOptions(
        int triggerHitCount,
        int triggerWindowSeconds,
        int eventCooldownSeconds,
        int maxEventDurationHours)
    {
        var create = () => new EventStateMachine(new EventStateMachineOptions
        {
            TriggerHitCount = triggerHitCount,
            TriggerWindowSeconds = triggerWindowSeconds,
            EventCooldownSeconds = eventCooldownSeconds,
            MaxEventDurationHours = maxEventDurationHours
        });

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_UsesDefaultTimingBehavior()
    {
        var machine = new EventStateMachine(new EventStateMachineOptions());

        machine.Process(Hit(BaseTime));
        var started = machine.Process(Hit(BaseTime.AddSeconds(5)));

        started.Should().Be(new EventTransition(
            BaseTime.AddSeconds(5),
            EventTransitionKind.Start,
            EventEndReason.None));

        var cooldownCompleted = machine.Process(Miss(BaseTime.AddSeconds(35)));
        cooldownCompleted.Should().Be(new EventTransition(
            BaseTime.AddSeconds(35),
            EventTransitionKind.Complete,
            EventEndReason.NoPerson));

        machine.Process(Hit(BaseTime.AddMinutes(1)));
        var restarted = machine.Process(Hit(BaseTime.AddMinutes(1)));
        restarted.Should().Be(new EventTransition(
            BaseTime.AddMinutes(1),
            EventTransitionKind.Start,
            EventEndReason.None));

        var maximumReached = BaseTime.AddMinutes(1).AddHours(2);
        var completedAndStarted = machine.Process(Hit(maximumReached));
        completedAndStarted.Should().Be(new EventTransition(
            maximumReached,
            EventTransitionKind.CompleteAndStart,
            EventEndReason.MaxDuration));
        machine.State.Should().Be(CameraEventState.Recording);
    }

    private static EventStateMachine CreateMachine() => new(new EventStateMachineOptions
    {
        TriggerHitCount = 2,
        TriggerWindowSeconds = 5,
        EventCooldownSeconds = 30,
        MaxEventDurationHours = 2
    });

    private static void StartEvent(EventStateMachine machine)
    {
        machine.Process(Hit(BaseTime));
        machine.Process(Hit(BaseTime));
    }

    private static DetectionSample Hit(DateTimeOffset timestampUtc) =>
        new(timestampUtc, PersonDetected: true, Confidence: 0.9f);

    private static DetectionSample Miss(DateTimeOffset timestampUtc) =>
        new(timestampUtc, PersonDetected: false, Confidence: 0f);
}
