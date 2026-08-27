using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class HookSetupSettingsStoreTests
{
    [TestMethod]
    public void SaveAndLoad_RemembersDetectedAndInstalledStatePerProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HookSetupSettingsStore(root);
            var settings = new HookSetupSettings
            {
                Providers = new Dictionary<string, HookSetupProviderSettings>
                {
                    ["codex"] = new() { IsDetected = true, IsHookInstalled = true },
                    ["cursor"] = new() { IsDetected = false, IsHookInstalled = false }
                }
            };

            Assert.IsTrue(store.TrySave(settings));

            var reloaded = new HookSetupSettingsStore(root).Load();
            Assert.IsTrue(reloaded.Providers["codex"].IsDetected);
            Assert.IsTrue(reloaded.Providers["codex"].IsHookInstalled);
            Assert.IsFalse(reloaded.Providers["cursor"].IsDetected);
            Assert.IsFalse(reloaded.Providers["cursor"].IsHookInstalled);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
