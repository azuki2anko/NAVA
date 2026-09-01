namespace RxV4A.Core;

public class YamahaException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class YamahaApiException(int responseCode)
    : YamahaException($"Yamaha API returned response_code={responseCode}.")
{
    public int ResponseCode { get; } = responseCode;
}

public sealed class YamahaProtocolException(string message, Exception? innerException = null)
    : YamahaException(message, innerException);

public sealed class CapabilityNotSupportedException(string capability)
    : YamahaException($"The connected device does not advertise capability '{capability}'.")
{
    public string Capability { get; } = capability;
}
