using AIUsageMonitor.Integrations;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class HookProtocolTests
{
    [TestMethod]
    public void TryReadNotification_AcceptsOwnedAntigravityNotification()
    {
        var arguments = HookProtocol.CreateArguments("antigravity");

        var parsed = HookProtocol.TryReadNotification(arguments, out var provider);

        Assert.IsTrue(parsed);
        Assert.AreEqual("antigravity", provider);
    }
}
