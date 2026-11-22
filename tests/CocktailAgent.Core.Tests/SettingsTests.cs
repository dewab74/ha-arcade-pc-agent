namespace CocktailAgent.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Parses_with_defaults()
    {
        var s = AgentSettings.Parse("""{ "host": "broker.example.org", "username": "cocktail-agent", "caFile": "C:\\ca.pem" }""");
        Assert.Equal(8883, s.Port);
        Assert.True(s.UseTls);
        Assert.Equal("cocktail_pc", s.NodeId);
        Assert.Equal(string.Empty, s.Password);
        Assert.Equal(@"C:\LaunchBox", s.LaunchBoxDir);
        Assert.False(s.HasInputWatch);
        Assert.Equal("switch.cocktail_pc_mute", s.EffectiveMuteEntityId);
        Assert.Equal("number.cocktail_pc_volume", s.EffectiveVolumeEntityId);
    }

    [Theory]
    [InlineData("muteEntityId", "number.x")]
    [InlineData("muteEntityId", "switch.Bad-Name")]
    [InlineData("volumeEntityId", "volume")]
    public void Pinned_entity_ids_must_match_their_domain(string key, string value) =>
        Assert.ThrowsAny<Exception>(() => AgentSettings.Parse($$"""{ "host": "h", "username": "u", "{{key}}": "{{value}}" }"""));

    [Fact]
    public void Unknown_keys_fail_loudly() =>
        Assert.ThrowsAny<Exception>(() => AgentSettings.Parse("""{ "host": "h", "username": "u", "pasword": "x" }"""));

    [Fact]
    public void Password_never_comes_from_the_file() =>
        Assert.ThrowsAny<Exception>(() => AgentSettings.Parse("""{ "host": "h", "username": "u", "password": "x" }"""));

    [Theory]
    [InlineData("""{ "username": "u" }""")]
    [InlineData("""{ "host": "h", "username": "u", "port": 0 }""")]
    [InlineData("""{ "host": "h", "username": "u", "nodeId": "Cocktail PC" }""")]
    public void Invalid_settings_are_rejected(string json) => Assert.ThrowsAny<Exception>(() => AgentSettings.Parse(json));

    [Fact]
    public void ToString_has_no_password()
    {
        var s = AgentSettings.Parse("""{ "host": "h", "username": "u" }""") with { Password = "hunter2" };  // pragma: allowlist secret
        Assert.DoesNotContain("hunter2", s.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trips_through_json_without_the_password()
    {
        var s = new AgentSettings
        {
            Host = "broker.example.org",
            Username = "agent",
            Password = "secret",
            CaFile = @"C:\ca.pem",
            SuggestedArea = "Game Room",
            InputWatchDir = @"C:\ProgramData\InputWatch",
            MuteEntityId = "switch.cabinet_muted",
        };
        var json = s.ToJson();
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("nasShare", json, StringComparison.Ordinal);
        Assert.DoesNotContain("effective", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(s with { Password = string.Empty }, AgentSettings.Parse(json));
    }
}
