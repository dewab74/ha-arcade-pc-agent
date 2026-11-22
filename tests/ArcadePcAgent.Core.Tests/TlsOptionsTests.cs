namespace ArcadePcAgent.Core.Tests;

public class TlsOptionsTests
{
    private static MQTTnet.MqttClientTlsOptions? Tls(AgentSettings settings)
    {
        var agent = new AgentService(settings, new FakeAudio(), new FakeSystem(), new FakeHealth(), new RecordingLog(), "1");
        var options = agent.BuildOptions();
        return ((MQTTnet.MqttClientTcpOptions)options.ChannelOptions).TlsOptions;
    }

    [Fact]
    public void Plain_mqtt_by_default()
    {
        var tls = Tls(new AgentSettings { Host = "broker" });
        Assert.True(tls is null || !tls.UseTls);
    }

    [Fact]
    public void Tls_with_the_system_trust_store_uses_no_custom_check()
    {
        var tls = Tls(new AgentSettings { Host = "broker", Port = 8883, UseTls = true });
        Assert.NotNull(tls);
        Assert.True(tls.UseTls);
        Assert.Null(tls.CertificateValidationHandler);
    }

    [Fact]
    public void Verification_off_accepts_any_certificate()
    {
        var tls = Tls(new AgentSettings { Host = "broker", Port = 8883, UseTls = true, VerifyCertificate = false });
        Assert.NotNull(tls?.CertificateValidationHandler);
        Assert.True(tls.CertificateValidationHandler(null!));
    }

    [Fact]
    public void No_credentials_without_a_username()
    {
        var agent = new AgentService(new AgentSettings { Host = "broker" }, new FakeAudio(), new FakeSystem(), new FakeHealth(), new RecordingLog(), "1");
        Assert.Null(agent.BuildOptions().Credentials);
    }
}
