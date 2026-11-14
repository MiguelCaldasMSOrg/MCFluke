namespace MCFluke;

internal static class NativeResult
{
    internal static void ThrowIfFailed(int result, NativeError error, string operation)
    {
        if (result != error.Status || !Enum.IsDefined((FlukeStatus)result))
        {
            throw new FlukeProtocolException(error.OsCode, operation,
                $"Invalid native status/error pair: returned {result}, structured status {error.Status}; " +
                error.GetMessage());
        }

        if (result == (int)FlukeStatus.Ok)
        {
            return;
        }

        string message = error.GetMessage();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = FlukeProtocol.GetStatusMessage((FlukeStatus)result);
        }

        throw CreateException((FlukeStatus)result, error.OsCode, operation, message);
    }

    internal static FlukeException CreateException(FlukeStatus status, int osCode, string operation, string message)
        => status switch
        {
            FlukeStatus.InvalidArgument => new FlukeInvalidArgumentException(osCode, operation, message),
            FlukeStatus.IoError => new FlukeIOException(osCode, operation, message),
            FlukeStatus.Timeout => new FlukeTimeoutException(osCode, operation, message),
            FlukeStatus.ProtocolError => new FlukeProtocolException(osCode, operation, message),
            FlukeStatus.CommandRejected => new FlukeCommandRejectedException(osCode, operation, message),
            FlukeStatus.BufferTooSmall => new FlukeBufferTooSmallException(osCode, operation, message),
            FlukeStatus.WrongModel => new FlukeWrongModelException(osCode, operation, message),
            FlukeStatus.ConfirmationRequired => new FlukeConfirmationRequiredException(osCode, operation, message),
            FlukeStatus.Unsupported => new FlukeUnsupportedException(osCode, operation, message),
            FlukeStatus.OutOfMemory => new FlukeOutOfMemoryException(osCode, operation, message),
            _ => new FlukeProtocolException(osCode, operation, $"Unexpected status {(int)status}: {message}")
        };
}
