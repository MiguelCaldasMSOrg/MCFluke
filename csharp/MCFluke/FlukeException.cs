namespace MCFluke;

public class FlukeException : Exception
{
    public FlukeStatus Status { get; }
    public int OperatingSystemErrorCode { get; }
    public string Operation { get; }
    public string NativeMessage { get; }

    internal FlukeException(FlukeStatus status, int osCode, string operation, string message)
        : base($"{operation}: {message} [status={(int)status} ({status}), OS={osCode}]")
    {
        Status = status;
        OperatingSystemErrorCode = osCode;
        Operation = operation;
        NativeMessage = message;
    }
}

public sealed class FlukeInvalidArgumentException : FlukeException
{
    internal FlukeInvalidArgumentException(int osCode, string operation, string message)
        : base(FlukeStatus.InvalidArgument, osCode, operation, message) { }
}

public sealed class FlukeIOException : FlukeException
{
    internal FlukeIOException(int osCode, string operation, string message)
        : base(FlukeStatus.IoError, osCode, operation, message) { }
}

public sealed class FlukeTimeoutException : FlukeException
{
    internal FlukeTimeoutException(int osCode, string operation, string message)
        : base(FlukeStatus.Timeout, osCode, operation, message) { }
}

public sealed class FlukeProtocolException : FlukeException
{
    internal FlukeProtocolException(int osCode, string operation, string message)
        : base(FlukeStatus.ProtocolError, osCode, operation, message) { }
}

public sealed class FlukeCommandRejectedException : FlukeException
{
    internal FlukeCommandRejectedException(int osCode, string operation, string message)
        : base(FlukeStatus.CommandRejected, osCode, operation, message) { }
}

public sealed class FlukeBufferTooSmallException : FlukeException
{
    internal FlukeBufferTooSmallException(int osCode, string operation, string message)
        : base(FlukeStatus.BufferTooSmall, osCode, operation, message) { }
}

public sealed class FlukeWrongModelException : FlukeException
{
    internal FlukeWrongModelException(int osCode, string operation, string message)
        : base(FlukeStatus.WrongModel, osCode, operation, message) { }
}

public sealed class FlukeConfirmationRequiredException : FlukeException
{
    internal FlukeConfirmationRequiredException(int osCode, string operation, string message)
        : base(FlukeStatus.ConfirmationRequired, osCode, operation, message) { }
}

public sealed class FlukeUnsupportedException : FlukeException
{
    internal FlukeUnsupportedException(int osCode, string operation, string message)
        : base(FlukeStatus.Unsupported, osCode, operation, message) { }
}

public sealed class FlukeOutOfMemoryException : FlukeException
{
    internal FlukeOutOfMemoryException(int osCode, string operation, string message)
        : base(FlukeStatus.OutOfMemory, osCode, operation, message) { }
}

public sealed class FlukeAbiException : NotSupportedException
{
    public uint ExpectedVersion { get; }
    public uint ActualVersion { get; }

    internal FlukeAbiException(uint actualVersion)
        : base($"MCFluke ABI {actualVersion} is incompatible with binding ABI {FlukeProtocol.SupportedAbiVersion}.")
    {
        ExpectedVersion = FlukeProtocol.SupportedAbiVersion;
        ActualVersion = actualVersion;
    }
}
