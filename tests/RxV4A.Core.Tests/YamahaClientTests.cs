using System.Net;
using System.Text;
using RxV4A.Core;

namespace RxV4A.Core.Tests;

public sealed class YamahaClientTests
{
    [Fact]
    public async Task GetDeviceInfo_AcceptsNumericFirmwareVersionsFromReceiver()
    {
        const string json = """
            {
              "response_code": 0,
              "model_name": "RX-V4A",
              "api_version": 2.15,
              "system_version": 1.67
            }
            """;
        var client = new YamahaClient(
            new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(json))),
            "receiver.test");

        var deviceInfo = await client.GetDeviceInfoAsync(CancellationToken.None);

        Assert.Equal("2.15", deviceInfo.ApiVersion);
        Assert.Equal("1.67", deviceInfo.SystemVersion);
    }

    [Fact]
    public async Task GetFeatures_DeserializesAdvertisedCapabilitiesAndIgnoresUnknownFields()
    {
        const string json = """
            {
              "response_code": 0,
              "unknown_root": { "future": true },
              "system": { "func_list": ["wired_lan"] },
              "zone": [
                {
                  "id": "main",
                  "func_list": ["power", "volume"],
                  "input_list": ["spotify", {"id":"hdmi1","future_value":42}],
                  "range_step": [{"id":"volume","min":-80.5,"max":16.5,"step":0.5}],
                  "scene_num": 4
                }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(json));
        var client = new YamahaClient(new HttpClient(handler), "receiver.test");

        var features = await client.GetFeaturesAsync(CancellationToken.None);
        var snapshot = new CapabilitySnapshot(
            new DeviceInfoResponse { ModelName = "RX-V4A" },
            features,
            new AdvancedFeaturesResponse());

        Assert.True(snapshot.SupportsZoneFunction("main", "power"));
        Assert.False(snapshot.SupportsZoneFunction("zone2", "power"));
        Assert.Equal(["spotify", "hdmi1"], snapshot.FindZone("main")!.Inputs.Select(input => input.Id));
        Assert.Equal(0.5m, snapshot.FindZone("main")!.Ranges.Single().Step);
    }

    [Theory]
    [InlineData(MainPower.On, "power=on")]
    [InlineData(MainPower.Standby, "power=standby")]
    public async Task SetMainPower_UsesOnlyTypedPowerValues(MainPower power, string expectedQuery)
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return JsonResponse("{\"response_code\":0}");
        });
        var client = new YamahaClient(new HttpClient(handler), "receiver.test", 55275);

        await client.SetMainPowerAsync(power, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.EndsWith($"/YamahaExtendedControl/v1/main/setPower?{expectedQuery}", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("RXV4AManager/1.0", captured.Headers.GetValues("X-AppName").Single());
        Assert.Equal("55275", captured.Headers.GetValues("X-AppPort").Single());
    }

    [Fact]
    public async Task InputAndSceneCommands_UseFixedEndpointsAndEncodedTypedValues()
    {
        var requestedUris = new List<Uri>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri!);
            return JsonResponse("{\"response_code\":0}");
        });
        var client = new YamahaClient(new HttpClient(handler), "receiver.test");

        await client.SetMainInputAsync("hdmi1&unexpected=true", CancellationToken.None);
        await client.RecallMainSceneAsync(4, CancellationToken.None);

        Assert.EndsWith(
            "/YamahaExtendedControl/v1/main/setInput?input=hdmi1%26unexpected%3Dtrue",
            requestedUris[0].AbsoluteUri);
        Assert.EndsWith(
            "/YamahaExtendedControl/v1/main/recallScene?num=4",
            requestedUris[1].AbsoluteUri);
    }

    [Fact]
    public async Task NonZeroResponseCode_ThrowsTypedApiException()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("{\"response_code\":4}"));
        var client = new YamahaClient(new HttpClient(handler), "receiver.test");

        var exception = await Assert.ThrowsAsync<YamahaApiException>(() =>
            client.GetMainZoneStatusAsync(CancellationToken.None));

        Assert.Equal(4, exception.ResponseCode);
    }

    [Fact]
    public async Task InvalidJson_ThrowsProtocolException()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("not-json"));
        var client = new YamahaClient(new HttpClient(handler), "receiver.test");

        await Assert.ThrowsAsync<YamahaProtocolException>(() =>
            client.GetDeviceInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingResponseCode_ThrowsProtocolException()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("{\"model_name\":\"RX-V4A\"}"));
        var client = new YamahaClient(new HttpClient(handler), "receiver.test");

        await Assert.ThrowsAsync<YamahaProtocolException>(() =>
            client.GetDeviceInfoAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("https://receiver.test")]
    [InlineData("http://receiver.test/arbitrary/path")]
    [InlineData("http://user:password@receiver.test")]
    public void CreateBaseUri_RejectsUnsafeHostShapes(string host) =>
        Assert.Throws<ArgumentException>(() => YamahaClient.CreateBaseUri(host));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
