namespace ResilienceLab;

public sealed class BrokenCircuitException : Exception
{
    public BrokenCircuitException()
        : base("The circuit is open or its recovery probe is already running.")
    {
    }
}
