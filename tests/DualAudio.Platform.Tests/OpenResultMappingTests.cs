using System.Runtime.InteropServices;
using DualAudio.Core.Errors;
using DualAudio.Platform.Windows.Bluetooth;
using Windows.Media.Audio;

namespace DualAudio.Platform.Tests;

public sealed class OpenResultMappingTests
{
    [Fact]
    public void Success_maps_to_ok()
    {
        var result = OpenResultMapping.FromStatus(AudioPlaybackConnectionOpenResultStatus.Success, 0);

        result.Success.Should().BeTrue();
        result.ErrorKind.Should().BeNull();
    }

    [Theory]
    [InlineData(AudioPlaybackConnectionOpenResultStatus.RequestTimedOut, ErrorKind.OpenTimedOut)]
    [InlineData(AudioPlaybackConnectionOpenResultStatus.DeniedBySystem, ErrorKind.DeniedBySystem)]
    [InlineData(AudioPlaybackConnectionOpenResultStatus.UnknownFailure, ErrorKind.Unknown)]
    [InlineData((AudioPlaybackConnectionOpenResultStatus)42, ErrorKind.Unknown)]
    public void Failure_statuses_map_to_error_kinds(AudioPlaybackConnectionOpenResultStatus status, ErrorKind expected)
    {
        var result = OpenResultMapping.FromStatus(status, 0);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(expected);
    }

    [Fact]
    public void Extended_error_HRESULT_goes_into_detail_only()
    {
        var result = OpenResultMapping.FromStatus(AudioPlaybackConnectionOpenResultStatus.UnknownFailure, unchecked((int)0x8007048F));

        result.ErrorKind.Should().Be(ErrorKind.Unknown);
        result.Detail.Should().Contain("0x8007048F");
    }

    [Fact]
    public void UnknownFailure_with_0x8007001F_maps_to_PhoneUnreachable()
    {
        // BUG-003: phone Bluetooth off / out of range surfaces as UnknownFailure + ERROR_GEN_FAILURE.
        var result = OpenResultMapping.FromStatus(AudioPlaybackConnectionOpenResultStatus.UnknownFailure, unchecked((int)0x8007001F));

        result.ErrorKind.Should().Be(ErrorKind.PhoneUnreachable);
        result.Detail.Should().Contain("0x8007001F");
    }

    [Theory]
    [InlineData(0x8007001Fu, ErrorKind.PhoneUnreachable)]
    [InlineData(0x80070490u, ErrorKind.DeviceNotFound)]
    [InlineData(0x8007048Fu, ErrorKind.DeviceNotFound)]
    [InlineData(0x800710DFu, ErrorKind.DeviceNotFound)]
    [InlineData(0x80070005u, ErrorKind.DeniedBySystem)]
    [InlineData(0x800705B4u, ErrorKind.OpenTimedOut)]
    [InlineData(0x80070079u, ErrorKind.OpenTimedOut)]
    [InlineData(0x80070032u, ErrorKind.SinkNotSupported)]
    [InlineData(0x80004005u, ErrorKind.Unknown)]
    public void Exceptions_map_by_HRESULT(uint hresult, ErrorKind expected)
    {
        var ex = new COMException("message mentioning AA:BB:CC:DD:EE:FF", unchecked((int)hresult));

        var result = OpenResultMapping.FromException("OpenAsync", ex);

        result.ErrorKind.Should().Be(expected);
        result.Detail.Should().Contain($"0x{hresult:X8}")
            .And.NotContain("AA:BB", "exception messages are never copied into Detail");
    }
}
