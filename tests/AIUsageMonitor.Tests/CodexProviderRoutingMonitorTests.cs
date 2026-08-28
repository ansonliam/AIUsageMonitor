using AIUsageMonitor.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class CodexProviderRoutingMonitorTests
{
    private const string ChatGptAuth = "{\"auth_mode\":\"chatgpt\",\"OPENAI_API_KEY\":null}";
    private const string ApiKeyAuth = "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-test\"}";

    [TestMethod]
    public void OpenAiProviderOnChatGptLogin_IsNotBilledPerToken()
    {
        const string config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"";

        Assert.IsFalse(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, ChatGptAuth));
    }

    [TestMethod]
    public void OpenAiProviderOnApiKeyLogin_IsBilledPerToken()
    {
        const string config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"";

        Assert.IsTrue(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, ApiKeyAuth));
    }

    [TestMethod]
    public void OpenAiProviderWithApiKeyInEnvironment_IsBilledPerToken()
    {
        const string config = "model_provider = \"openai\"";

        Assert.IsTrue(CodexProviderRoutingMonitor.ContentUsesApiProvider(
            config,
            authJson: null,
            getEnvironmentVariable: key => key == "OPENAI_API_KEY" ? "sk-test" : null));
    }

    [TestMethod]
    public void CustomProvider_IsBilledPerTokenRegardlessOfLogin()
    {
        const string config = "model_provider = \"azure_business\"\nmodel = \"gpt-5.6-sol\"";

        Assert.IsTrue(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, ChatGptAuth));
    }

    [TestMethod]
    public void MissingOrCommentedProvider_FallsBackToTheBuiltInOpenAiDefault()
    {
        const string config = "# model_provider = \"azure_business\"\nmodel = \"gpt-5.6-sol\"";

        Assert.IsFalse(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, ChatGptAuth));
        Assert.IsFalse(CodexProviderRoutingMonitor.ContentUsesApiProvider(null, ChatGptAuth));
    }

    [TestMethod]
    public void ProviderDefinedInsideAnUnselectedTable_IsNotTheActiveRoute()
    {
        const string config = """
            model_provider = "openai"

            [profiles.work]
            model_provider = "azure_business"
            """;

        Assert.IsFalse(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, ChatGptAuth));
    }

    [TestMethod]
    public void ProviderFromTheSelectedProfile_WinsOverTheTopLevelKey()
    {
        const string config = """
            profile = "work"
            model_provider = "openai"

            [profiles.work]
            model_provider = "azure_business"
            """;

        Assert.IsTrue(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, ChatGptAuth));
    }

    [TestMethod]
    public void UnreadableAuthFile_FailsClosedOnTheBuiltInProvider()
    {
        const string config = "model_provider = \"openai\"";

        Assert.IsFalse(CodexProviderRoutingMonitor.ContentUsesApiProvider(config, "{not json"));
    }

    [TestMethod]
    public async Task ConfigFileChange_UpdatesRoutingState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ai-usage-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.toml");

        try
        {
            File.WriteAllText(configPath, "model_provider = \"openai\"");
            File.WriteAllText(Path.Combine(directory, "auth.json"), ChatGptAuth);
            using var monitor = new CodexProviderRoutingMonitor(
                directory,
                NullLogger<CodexProviderRoutingMonitor>.Instance,
                _ => null);
            Assert.IsFalse(monitor.IsApiProvider);

            var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.RoutingChanged += value => changed.TrySetResult(value);
            File.WriteAllText(configPath, "model_provider = \"azure_business\"");

            Assert.IsTrue(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(monitor.IsApiProvider);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoginChange_UpdatesRoutingStateOnTheBuiltInProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ai-usage-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var authPath = Path.Combine(directory, "auth.json");

        try
        {
            File.WriteAllText(Path.Combine(directory, "config.toml"), "model_provider = \"openai\"");
            File.WriteAllText(authPath, ChatGptAuth);
            using var monitor = new CodexProviderRoutingMonitor(
                directory,
                NullLogger<CodexProviderRoutingMonitor>.Instance,
                _ => null);
            Assert.IsFalse(monitor.IsApiProvider);

            var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.RoutingChanged += value => changed.TrySetResult(value);
            File.WriteAllText(authPath, ApiKeyAuth);

            Assert.IsTrue(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(monitor.IsApiProvider);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
