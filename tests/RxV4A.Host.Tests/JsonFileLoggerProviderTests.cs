using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace RxV4A.Host.Tests;

public sealed class JsonFileLoggerProviderTests
{
    [Fact]
    public void NonSerializableStructuredProperty_DoesNotBreakLogging()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new JsonFileLoggerProvider(root);
            var logger = provider.CreateLogger("test");
            var metadata = typeof(JsonFileLoggerProviderTests).GetMethods();

            var exception = Record.Exception(() =>
                logger.LogInformation("Endpoint metadata {Metadata}", metadata));

            Assert.Null(exception);
            var log = Directory.GetFiles(root, "*.jsonl").Single();
            using var entry = JsonDocument.Parse(File.ReadAllText(log));
            Assert.Equal(JsonValueKind.String,
                entry.RootElement.GetProperty("properties").GetProperty("Metadata").ValueKind);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
