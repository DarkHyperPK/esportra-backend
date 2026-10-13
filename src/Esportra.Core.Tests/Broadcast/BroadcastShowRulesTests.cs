using Esportra.Core.Broadcast;
using Xunit;

namespace Esportra.Core.Tests.Broadcast;

public sealed class BroadcastShowRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_title_is_rejected(string? title) => Assert.Equal("title_must_be_1_to_200_characters", BroadcastShowRules.ValidateTitleChange("draft", "Old", title));

    [Fact]
    public void Overlong_title_is_rejected() => Assert.NotNull(BroadcastShowRules.ValidateTitleChange("draft", "Old", new string('a', 201)));

    [Fact]
    public void Live_title_change_is_locked() => Assert.Equal("show_live_title_locked", BroadcastShowRules.ValidateTitleChange("live", "Final", "New"));

    [Fact]
    public void Same_live_title_does_not_block_other_changes() => Assert.Null(BroadcastShowRules.ValidateTitleChange("live", "Final", " Final "));

    [Fact]
    public void Draft_title_is_editable() => Assert.Null(BroadcastShowRules.ValidateTitleChange("draft", "Old", "New"));
}
