using RxV4A.Core;

namespace RxV4A.Host;

public interface IYamahaClientFactory
{
    IYamahaClient Create(string host);
}

public sealed class YamahaClientFactory(IHttpClientFactory httpClientFactory) : IYamahaClientFactory
{
    public IYamahaClient Create(string host) =>
        new YamahaClient(httpClientFactory.CreateClient(LocalApiApplication.YamahaHttpClientName), host);
}
