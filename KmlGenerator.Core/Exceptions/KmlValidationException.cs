namespace KmlGenerator.Core.Exceptions;

/// <summary>
/// Raised when a request cannot be processed safely.
/// </summary>
public sealed class KmlValidationException : Exception
{
    public KmlValidationException(string message)
        : base(message)
    {
    }
}
