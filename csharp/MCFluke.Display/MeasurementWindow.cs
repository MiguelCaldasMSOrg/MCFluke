using System.Drawing.Text;

namespace MCFluke.Display;

public partial class MeasurementWindow : Form
{
    private const int WmTouch = 0x0240;
    private const int WmPointerDown = 0x0246;
    private const int WsCaption = 0x00C00000;
    private readonly string? port;
    private readonly CancellationTokenSource stop = new();
    private Task readingTask = Task.CompletedTask;
    private FlukeReading? lastPublishedReading;
    private bool closing;
    private MeasurementText current = new("+1.234", "V DC");
    private string? errorMessage;

    public MeasurementWindow()
    {
        InitializeComponent();
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    internal MeasurementWindow(string port, string? argumentError = null) : this()
    {
        this.port = port;
        errorMessage = argumentError;
        ExitCode = argumentError is null ? 0 : 2;
        current = new MeasurementText("Connecting", "");
        Bounds = InitialBounds(Screen.FromPoint(Cursor.Position).Bounds);
        AccessibleName = argumentError ?? "Connecting to Fluke";
    }

    internal int ExitCode { get; private set; }

    internal static Rectangle InitialBounds(Rectangle screen)
    {
        int width = screen.Width / 3;
        int height = screen.Height / 3;
        return new Rectangle(screen.X + (screen.Width - width) / 2,
            screen.Y + (screen.Height - height) / 2, width, height);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style &= ~WsCaption;
            return parameters;
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (port is null || DesignMode || errorMessage is not null)
        {
            return;
        }
        readingTask = ReadUntilStoppedAsync(port);
        await readingTask;
    }

    private async Task ReadUntilStoppedAsync(string selectedPort)
    {
        try
        {
            await MeasurementReader.ReadAsync(selectedPort, PublishReadingAsync, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is FlukeException or FlukeAbiException or DllNotFoundException or
              BadImageFormatException or EntryPointNotFoundException or TypeInitializationException or
              FileLoadException or ArgumentException)
        {
            Exception cause = exception.GetBaseException();
            errorMessage = $"{cause.GetType().Name}: {cause.Message}";
            ExitCode = 1;
            Console.Error.WriteLine(errorMessage);
            AccessibleName = errorMessage;
            Invalidate();
        }
    }

    private Task PublishReadingAsync(FlukeReading reading, CancellationToken token)
    {
        if (reading == lastPublishedReading)
        {
            return Task.CompletedTask;
        }
        lastPublishedReading = reading;
        return InvokeAsync(() => ApplyReading(reading), token);
    }

    private void ApplyReading(FlukeReading reading)
    {
        if (closing || errorMessage is not null)
        {
            return;
        }
        current = MeasurementText.FromReading(reading);
        AccessibleName = $"{current.Value} {current.Unit}".TrimEnd();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RectangleF available = ClientRectangle;
        available.Inflate(-8, -8);
        if (available.Width <= 0 || available.Height <= 0)
        {
            return;
        }
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (errorMessage is not null)
        {
            using var errorFont = new Font(Font.FontFamily,
                Math.Clamp(available.Height / 10, 8, 18), FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisWord
            };
            e.Graphics.DrawString(errorMessage, errorFont, Brushes.OrangeRed, available, format);
            return;
        }
        using MeasurementLayout layout = MeasurementLayout.Fit(e.Graphics, Font.FontFamily, current, available);
        using var brush = new SolidBrush(ForeColor);
        layout.Draw(e.Graphics, current, brush);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Close();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPointerDown)
        {
            Close();
            return;
        }
        if (m.Msg == WmTouch)
        {
            base.WndProc(ref m);
            Close();
            return;
        }
        base.WndProc(ref m);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
        {
            return;
        }
        e.Cancel = !readingTask.IsCompleted;
        if (!closing)
        {
            await StopAndCloseAsync(e.Cancel);
        }
    }

    private async Task StopAndCloseAsync(bool closeAfterReading)
    {
        closing = true;
        Hide();
        stop.Cancel();
        await readingTask;
        if (closeAfterReading)
        {
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            stop.Cancel();
            stop.Dispose();
        }
        base.Dispose(disposing);
    }

}
