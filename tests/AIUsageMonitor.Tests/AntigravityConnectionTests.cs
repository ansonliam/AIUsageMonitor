using AIUsageMonitor.Integrations;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AntigravityConnectionTests
{
    [TestMethod]
    public void ParseConnection_ReadsCurrentDesktopArguments()
    {
        var connection = AntigravityLanguageServerClient.ParseConnection(
            "language_server.exe --override_ide_name antigravity --https_server_port 54321 --csrf_token example");

        Assert.IsNotNull(connection);
        Assert.AreEqual(54321, connection.Port);
        Assert.AreEqual("example", connection.CsrfToken);
    }

    [TestMethod]
    public void ParseConnection_SupportsEqualsAndQuotedValues()
    {
        var connection = AntigravityLanguageServerClient.ParseConnection(
            "language_server.exe --override_ide_name=antigravity --https_server_port=12345 --csrf_token=\"example value\"");

        Assert.IsNotNull(connection);
        Assert.AreEqual(12345, connection.Port);
        Assert.AreEqual("example value", connection.CsrfToken);
    }

    [TestMethod]
    public void ParseConnection_IgnoresOtherLanguageServers()
    {
        Assert.IsNull(AntigravityLanguageServerClient.ParseConnection(
            "language_server.exe --https_server_port 12345 --csrf_token example"));
    }

    [TestMethod]
    public void ParseConnection_DynamicPortZero_KeepsTokenForListenerDiscovery()
    {
        // Current Antigravity builds advertise a dynamic port as 0 and bind an OS-assigned
        // port; the connection must still parse so the real port can be discovered later.
        var connection = AntigravityLanguageServerClient.ParseConnection(
            "language_server.exe --standalone --override_ide_name antigravity " +
            "--https_server_port 0 --csrf_token example-csrf-token");

        Assert.IsNotNull(connection);
        Assert.AreEqual(0, connection.Port);
        Assert.AreEqual("example-csrf-token", connection.CsrfToken);
    }

    [TestMethod]
    public void ParseConnection_MissingCsrfToken_ReturnsNull()
    {
        Assert.IsNull(AntigravityLanguageServerClient.ParseConnection(
            "language_server.exe --override_ide_name antigravity --https_server_port 0"));
    }
}
