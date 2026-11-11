using DualAudio.Platform.Windows.Bluetooth;

namespace DualAudio.Platform.Tests;

public sealed class BluetoothDeviceNameResolverTests
{
    private static readonly Guid Phone = Guid.NewGuid();
    private static readonly Guid Speaker = Guid.NewGuid();

    [Fact]
    public void Picks_the_paired_device_in_the_same_container() =>
        BluetoothDeviceNameResolver.PickName(Phone, [(Speaker, "Party Boom Plus"), (Phone, "Shlawg")])
            .Should().Be("Shlawg");

    [Fact]
    public void No_match_returns_null_so_the_interface_name_is_used() =>
        BluetoothDeviceNameResolver.PickName(Phone, [(Speaker, "Party Boom Plus"), (null, "x")])
            .Should().BeNull();

    [Fact]
    public void Blank_names_are_skipped() =>
        BluetoothDeviceNameResolver.PickName(Phone, [(Phone, " "), (Phone, "Shlawg")])
            .Should().Be("Shlawg");
}
