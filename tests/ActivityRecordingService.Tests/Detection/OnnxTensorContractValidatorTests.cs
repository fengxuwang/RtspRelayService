using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Detection;

public sealed class OnnxTensorContractValidatorTests
{
    [Fact]
    public void Validate_AcceptsFloatYoloContractMatchingConfiguredDimensions()
    {
        var validate = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640]),
            CreateMetadata([1, 5, 8400]),
            new DetectionOptions());

        validate.Should().NotThrow();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Validate_RejectsUnexpectedInputRank(int rank)
    {
        var shape = Enumerable.Repeat(1L, rank).ToArray();

        var validate = () => OnnxTensorContractValidator.Validate(
            CreateMetadata(shape),
            CreateMetadata([1, 5, 8400]),
            new DetectionOptions());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage($"Expected input tensor rank 4 but found {rank}.*");
    }

    [Fact]
    public void Validate_RejectsNonFloatInputOrOutput()
    {
        var validate = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640], typeof(byte)),
            CreateMetadata([1, 5, 8400]),
            new DetectionOptions());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*Model input must be a float tensor*");

        validate = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640]),
            CreateMetadata([1, 5, 8400], typeof(double)),
            new DetectionOptions());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*Model output must be a float tensor*");
    }

    [Theory]
    [InlineData(359, 640)]
    [InlineData(360, 641)]
    public void Validate_RejectsStaticDimensionMismatchWithClearMessage(
        long height,
        long width)
    {
        var validate = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, height, width]),
            CreateMetadata([1, 5, 8400]),
            new DetectionOptions());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("Model input dimensions do not match Detection Width and Height.*configured=360x640*");
    }

    [Fact]
    public void Validate_RejectsInvalidOutputRankDimensionsAndClassChannel()
    {
        var act = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640]),
            CreateMetadata([5, 8400]),
            new DetectionOptions());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Expected output tensor rank 3 but found 2.*");

        act = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640]),
            CreateMetadata([2, 5, 8400]),
            new DetectionOptions());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Output batch must be 1 or dynamic*");

        act = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640]),
            CreateMetadata([1, 4, 8400]),
            new DetectionOptions());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("PersonClassId 0 is outside the model output channels.*");

        act = () => OnnxTensorContractValidator.Validate(
            CreateMetadata([1, 3, 360, 640]),
            CreateMetadata([1, -1, 8400]),
            new DetectionOptions());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Output channel count must be static*");
    }

    private static OnnxTensorMetadata CreateMetadata(long[] shape, Type? type = null) =>
        new(shape, type ?? typeof(float));
}
