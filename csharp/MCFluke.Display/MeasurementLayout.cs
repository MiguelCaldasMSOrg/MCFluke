namespace MCFluke.Display;

internal sealed record MeasurementText(string Value, string Unit)
{
    internal static MeasurementText FromReading(FlukeReading reading)
    {
        string display = reading.Display.Trim();
        if (reading.Unit.Length > 0 && display.EndsWith(reading.Unit, StringComparison.Ordinal))
        {
            display = display[..^reading.Unit.Length].TrimEnd();
        }
        return new MeasurementText(display, reading.Unit);
    }
}

internal sealed class MeasurementLayout : IDisposable
{
    private MeasurementLayout(Font valueFont, Font unitFont, PointF valueOrigin,
        PointF unitOrigin, RectangleF bounds)
    {
        ValueFont = valueFont;
        UnitFont = unitFont;
        ValueOrigin = valueOrigin;
        UnitOrigin = unitOrigin;
        Bounds = bounds;
    }

    internal Font ValueFont { get; }
    internal Font UnitFont { get; }
    internal PointF ValueOrigin { get; }
    internal PointF UnitOrigin { get; }
    internal RectangleF Bounds { get; }

    internal static MeasurementLayout Fit(Graphics graphics, FontFamily family,
        MeasurementText text, RectangleF available)
    {
        float low = 0;
        float high = available.Height * 2;
        for (int step = 0; step < 18; step++)
        {
            float candidate = (low + high) / 2;
            using var value = new Font(family, candidate, FontStyle.Regular, GraphicsUnit.Pixel);
            using var unit = new Font(family, candidate / 2, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = Measure(graphics, text, value, unit);
            if (measured.Size.Width <= available.Width && measured.Size.Height <= available.Height)
            {
                low = candidate;
            }
            else
            {
                high = candidate;
            }
        }

        var valueFont = new Font(family, low, FontStyle.Regular, GraphicsUnit.Pixel);
        var unitFont = new Font(family, low / 2, FontStyle.Regular, GraphicsUnit.Pixel);
        var final = Measure(graphics, text, valueFont, unitFont);
        float x = available.X + (available.Width - final.Size.Width) / 2;
        float y = available.Y + (available.Height - final.Size.Height) / 2;
        return new MeasurementLayout(valueFont, unitFont, new PointF(x, y),
            new PointF(x + final.ValueWidth + final.Gap, y + final.UnitOffset),
            new RectangleF(x, y, final.Size.Width, final.Size.Height));
    }

    internal void Draw(Graphics graphics, MeasurementText text, Brush brush)
    {
        graphics.DrawString(text.Value, ValueFont, brush, ValueOrigin, StringFormat.GenericTypographic);
        if (text.Unit.Length > 0)
        {
            graphics.DrawString(text.Unit, UnitFont, brush, UnitOrigin, StringFormat.GenericTypographic);
        }
    }

    private static (SizeF Size, float ValueWidth, float Gap, float UnitOffset) Measure(
        Graphics graphics, MeasurementText text, Font valueFont, Font unitFont)
    {
        SizeF valueSize = graphics.MeasureString(text.Value, valueFont, PointF.Empty, StringFormat.GenericTypographic);
        SizeF unitSize = text.Unit.Length == 0 ? SizeF.Empty :
            graphics.MeasureString(text.Unit, unitFont, PointF.Empty, StringFormat.GenericTypographic);
        float gap = text.Unit.Length == 0 ? 0 : valueFont.Size / 8;
        float ascent = (float)valueFont.FontFamily.GetCellAscent(valueFont.Style) /
            valueFont.FontFamily.GetEmHeight(valueFont.Style);
        float unitOffset = (valueFont.Size - unitFont.Size) * ascent;
        return (new SizeF(valueSize.Width + gap + unitSize.Width,
            Math.Max(valueSize.Height, unitOffset + unitSize.Height)),
            valueSize.Width, gap, unitOffset);
    }

    public void Dispose()
    {
        ValueFont.Dispose();
        UnitFont.Dispose();
    }
}
