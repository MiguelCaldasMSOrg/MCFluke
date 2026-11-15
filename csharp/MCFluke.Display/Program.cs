namespace MCFluke.Display;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--self-test"])
        {
            return SelfTests.Run();
        }

        string port = "COM3";
        string? argumentError = null;
        try
        {
            port = ParsePort(args);
        }
        catch (ArgumentException exception)
        {
            argumentError = exception.Message;
        }

        using var window = new MeasurementWindow(port, argumentError);
        Application.Run(window);
        return window.ExitCode;
    }

    internal static string ParsePort(string[] args) => args switch
    {
        [] => "COM3",
        ["--port", var port] when !string.IsNullOrWhiteSpace(port) => port,
        _ => throw new ArgumentException(
            "Usage: MCFluke.Display [--port COM3|sim://187], or MCFluke.Display --self-test. " +
            "The simulator and self-tests must be selected explicitly.")
    };
}
