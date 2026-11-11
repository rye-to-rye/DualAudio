using DualAudio.Core.Errors;

namespace DualAudio.Core.Tests.Errors;

public sealed class ErrorMessagesTests
{
    public static TheoryData<ErrorKind> AllKinds => [.. Enum.GetValues<ErrorKind>()];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_kind_has_a_specific_title_and_action(ErrorKind kind)
    {
        var message = ErrorMessages.For(kind);

        message.Title.Should().NotBeNullOrWhiteSpace();
        message.Action.Should().NotBeNullOrWhiteSpace().And.EndWith(".");
        message.Should().NotBe(ErrorMessages.For((ErrorKind)(-1)), "each kind needs its own entry, not the fallback");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Titles_are_short_and_contain_no_technical_codes(ErrorKind kind)
    {
        var title = ErrorMessages.For(kind).Title;

        title.Length.Should().BeLessThanOrEqualTo(40);
        title.Should().NotContainAny("0x", "HRESULT", "Exception", "E_", nameof(ErrorKind));
    }

    [Fact]
    public void Titles_are_unique()
    {
        Enum.GetValues<ErrorKind>().Select(k => ErrorMessages.For(k).Title).Should().OnlyHaveUniqueItems();
    }
}
