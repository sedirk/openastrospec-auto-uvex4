namespace UvexAdv.Phd2;

public class Phd2Exception : Exception
{
    public Phd2Exception(string message)
        : base(message)
    {
    }

    public Phd2Exception(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class Phd2DisconnectedException : Phd2Exception
{
    public Phd2DisconnectedException(string message)
        : base(message)
    {
    }

    public Phd2DisconnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class Phd2RpcException : Phd2Exception
{
    public Phd2RpcException(int code, string message, string? data = null)
        : base($"PHD2 JSON-RPC error {code}: {message}")
    {
        Code = code;
        RpcMessage = message;
        DataJson = data;
    }

    public int Code { get; }

    public string RpcMessage { get; }

    public string? DataJson { get; }
}

public sealed class Phd2CommandTimeoutException : TimeoutException
{
    public Phd2CommandTimeoutException(string operation, TimeSpan timeout)
        : base($"PHD2 operation '{operation}' did not complete within {timeout}.")
    {
        Operation = operation;
        Timeout = timeout;
    }

    public string Operation { get; }

    public TimeSpan Timeout { get; }
}

public sealed class Phd2IdentityMismatchException : Phd2Exception
{
    public Phd2IdentityMismatchException(Phd2IdentityValidation validation)
        : base($"PHD2 identity validation did not pass: {string.Join("; ", validation.Failures.Concat(validation.IndeterminateReasons))}")
    {
        Validation = validation;
    }

    public Phd2IdentityValidation Validation { get; }
}

public sealed class Phd2CalibrationRejectedException : Phd2Exception
{
    public Phd2CalibrationRejectedException(string message, Phd2CalibrationValidation? validation = null)
        : base(message)
    {
        Validation = validation;
    }

    public Phd2CalibrationValidation? Validation { get; }
}

public sealed class Phd2AutomationPausedException : Phd2Exception
{
    public Phd2AutomationPausedException()
        : base("The UVEX automation coordinator is paused; no new mutating PHD2 operation was sent.")
    {
    }
}

public sealed class Phd2CaptureException : Phd2Exception
{
    public Phd2CaptureException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The one allowed exact lock-position mutation may have taken effect, so the
/// caller must reconcile with a fresh get_lock_position and its durable ledger.
/// Automatic resend is explicitly forbidden.
/// </summary>
public sealed class Phd2LockPositionReconciliationRequiredException : Phd2Exception
{
    public Phd2LockPositionReconciliationRequiredException(
        string message,
        Phd2Point before,
        Phd2Point requested,
        Phd2Point? observed,
        bool mutationResponseReceived,
        Exception innerException)
        : base(message, innerException)
    {
        Before = before;
        Requested = requested;
        Observed = observed;
        MutationResponseReceived = mutationResponseReceived;
    }

    public Phd2Point Before { get; }

    public Phd2Point Requested { get; }

    public Phd2Point? Observed { get; }

    public bool MutationResponseReceived { get; }

    public bool ReconciliationRequired => true;

    public bool AutomaticRetryAllowed => false;
}
