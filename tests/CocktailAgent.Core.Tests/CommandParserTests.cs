namespace CocktailAgent.Core.Tests;

public class CommandParserTests
{
    private static readonly Topics T = new("homeassistant", "cocktail_pc");

    [Theory]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    [InlineData(" On ", true)]
    public void Mute_payloads(string payload, bool muted) =>
        Assert.Equal(new SetMutedCommand(muted), CommandParser.Parse(T, T.MuteSet, payload, out _));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("37.5", 38)]
    [InlineData("42.0", 42)]
    public void Volume_payloads(string payload, int volume) =>
        Assert.Equal(new SetVolumeCommand(volume), CommandParser.Parse(T, T.VolumeSet, payload, out _));

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("loud")]
    [InlineData("NaN")]
    [InlineData("")]
    public void Bad_volume_is_rejected(string payload)
    {
        Assert.Null(CommandParser.Parse(T, T.VolumeSet, payload, out var why));
        Assert.Contains("0-100", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    public void Screen_payloads(string payload, bool on) =>
        Assert.Equal(new SetScreenCommand(on), CommandParser.Parse(T, T.ScreenSet, payload, out _));

    [Fact]
    public void Bad_screen_is_rejected()
    {
        Assert.Null(CommandParser.Parse(T, T.ScreenSet, "dim", out var why));
        Assert.Contains("screen", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_mute_is_rejected() => Assert.Null(CommandParser.Parse(T, T.MuteSet, "yes", out _));

    [Theory]
    [InlineData(AgentButton.CloseEmulators)]
    [InlineData(AgentButton.CloseFrontend)]
    [InlineData(AgentButton.RestartBigBox)]
    [InlineData(AgentButton.Shutdown)]
    [InlineData(AgentButton.Reboot)]
    public void Buttons_need_PRESS(AgentButton button)
    {
        Assert.Equal(new PressCommand(button), CommandParser.Parse(T, T.Command(button), "PRESS", out _));
        Assert.Null(CommandParser.Parse(T, T.Command(button), "press", out _));
        Assert.Null(CommandParser.Parse(T, T.Command(button), "", out _));
    }

    [Fact]
    public void Unknown_button_and_topic_are_rejected()
    {
        Assert.Null(CommandParser.Parse(T, "cocktail-agent/cocktail_pc/command/format_c", "PRESS", out var why));
        Assert.Contains("unknown command", why, StringComparison.Ordinal);
        Assert.Null(CommandParser.Parse(T, "somewhere/else", "ON", out _));
    }
}
