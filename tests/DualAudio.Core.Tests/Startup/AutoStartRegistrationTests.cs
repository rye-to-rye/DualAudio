using DualAudio.Core.Startup;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.Core.Tests.Startup;

public sealed class AutoStartRegistrationTests
{
    private const string Exe = @"C:\Program Files\DualAudio\DualAudio.exe";
    private const string Command = "\"C:\\Program Files\\DualAudio\\DualAudio.exe\" --minimized";

    private readonly FakeAutoStartEntry _entry = new();

    private AutoStartRegistration Sut => new(_entry);

    [Fact]
    public void Command_quotes_the_path_and_adds_minimized()
    {
        AutoStartRegistration.CommandFor(Exe).Should().Be(Command);
        AutoStartRegistration.CommandFor("\"" + Exe + "\"").Should().Be(Command, "already quoted paths aren't double-quoted");
    }

    [Fact]
    public void Enabling_writes_the_entry()
    {
        Sut.Sync(true, Exe).Should().Be(AutoStartSyncResult.Written);

        _entry.Value.Should().Be(Command);
    }

    [Fact]
    public void Same_path_is_left_alone()
    {
        _entry.Value = Command;

        Sut.Sync(true, Exe).Should().Be(AutoStartSyncResult.Unchanged);

        _entry.Writes.Should().Be(0);
    }

    [Fact]
    public void Moved_exe_is_rewritten_on_start()
    {
        _entry.Value = "\"C:\\Old place\\DualAudio.exe\" --minimized";

        Sut.Sync(true, @"D:\Apps\DualAudio\DualAudio.exe").Should().Be(AutoStartSyncResult.Written);

        _entry.Value.Should().Be("\"D:\\Apps\\DualAudio\\DualAudio.exe\" --minimized");
    }

    [Fact]
    public void Disabling_removes_the_entry_and_is_a_no_op_without_one()
    {
        _entry.Value = Command;
        Sut.Sync(false, Exe).Should().Be(AutoStartSyncResult.Removed);
        _entry.Value.Should().BeNull();

        Sut.Sync(false, Exe).Should().Be(AutoStartSyncResult.Unchanged);
    }

    [Fact]
    public void Registry_failures_and_unknown_exe_path_never_throw()
    {
        Sut.Sync(true, null).Should().Be(AutoStartSyncResult.Failed);

        _entry.Throw = true;
        Sut.Sync(true, Exe).Should().Be(AutoStartSyncResult.Failed);
        Sut.Sync(false, Exe).Should().Be(AutoStartSyncResult.Failed);
    }

    [Theory]
    [InlineData(new[] { "--minimized" }, true)]
    [InlineData(new[] { "--MINIMIZED" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--other" }, false)]
    public void Minimized_argument_is_detected(string[] args, bool minimized)
    {
        AutoStartRegistration.IsMinimized(args).Should().Be(minimized);
    }
}
