namespace MCFluke.Display;

internal static class MeasurementReader
{
    internal static Task ReadAsync(string port,
        Func<FlukeReading, CancellationToken, Task> publish, CancellationToken token)
        => Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            using FlukeDevice meter = FlukeDevice.Open(port);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                FlukeReading reading = meter.ReadDisplay();
                token.ThrowIfCancellationRequested();
                await publish(reading, token).ConfigureAwait(false);
            }
        }, token);
}
