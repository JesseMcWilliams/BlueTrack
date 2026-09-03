namespace BlueTrack.Api.Secrets;

public sealed class SecretRetrievalException(string message, CyberArkErrorCategory category, Exception? inner = null)
    : Exception(message, inner)
{
    public CyberArkErrorCategory Category { get; } = category;
}
