using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace NetWatch.Web.Reports;

/// <summary>Lays a <see cref="ReportDocument"/> out on A4 pages.</summary>
public static class PdfReportRenderer
{
    private const double Margin = 40;
    private const double ContentWidth = PdfWriter.PageWidth - (2 * Margin);
    private const double Bottom = PdfWriter.PageHeight - 50;

    private static readonly (double, double, double) Muted = (0.42, 0.45, 0.5);
    private static readonly (double, double, double) Rule = (0.82, 0.84, 0.87);
    private static readonly (double, double, double) HeaderFill = (0.92, 0.94, 0.96);
    private static readonly (double, double, double) Zebra = (0.975, 0.98, 0.985);
    private static readonly (double, double, double) Accent = (0.13, 0.4, 0.75);
    private static readonly (double, double, double) Good = (0.16, 0.6, 0.33);
    private static readonly (double, double, double) Warn = (0.85, 0.55, 0.1);
    private static readonly (double, double, double) Bad = (0.8, 0.2, 0.2);

    public static byte[] Render(ReportDocument doc)
    {
        var pdf = new PdfWriter();
        double y = 0;
        void NewPage()
        {
            pdf.NewPage();
            y = Margin;
        }

        bool Fits(double height) => y + height <= Bottom;

        NewPage();
        pdf.Rect(Margin, y, ContentWidth, 3, Accent);
        y += 10;
        pdf.Text(Margin, y, doc.Title, 18, bold: true);
        y += 24;
        pdf.Text(Margin, y, doc.Subtitle, 9.5, color: Muted);
        y += 22;

        foreach (var section in doc.Sections)
        {
            if (!Fits(60))
            {
                NewPage();
            }

            pdf.Text(Margin, y, section.Heading, 12.5, bold: true);
            y += 17;
            if (section.Note is { } note)
            {
                foreach (var line in PdfWriter.Wrap(note, ContentWidth, 8.5))
                {
                    if (!Fits(12))
                    {
                        NewPage();
                    }

                    pdf.Text(Margin, y, line, 8.5, color: Muted);
                    y += 11;
                }

                y += 4;
            }

            // Key figures: four per row.
            if (section.Summary.Count > 0)
            {
                const int perRow = 4;
                var cell = ContentWidth / perRow;
                for (var i = 0; i < section.Summary.Count; i += perRow)
                {
                    if (!Fits(34))
                    {
                        NewPage();
                    }

                    for (var j = 0; j < perRow && i + j < section.Summary.Count; j++)
                    {
                        var (label, value) = section.Summary[i + j];
                        var x = Margin + (j * cell);
                        pdf.Text(x, y, PdfWriter.Fit(label.ToUpperInvariant(), cell - 8, 7), 7, color: Muted);
                        pdf.Text(x, y + 10, PdfWriter.Fit(value, cell - 8, 13, bold: true), 13, bold: true);
                    }

                    y += 34;
                }

                y += 4;
            }

            if (section.Bars is { Items.Count: > 0 } bars)
            {
                const double labelWidth = 170, valueWidth = 70, rowHeight = 13;
                var barWidth = ContentWidth - labelWidth - valueWidth - 10;
                foreach (var (label, value, text) in bars.Items)
                {
                    if (!Fits(rowHeight))
                    {
                        NewPage();
                    }

                    pdf.Text(Margin, y + 1, PdfWriter.Fit(label, labelWidth - 6, 8), 8);
                    var fraction = bars.Max <= 0 ? 0 : Math.Clamp(value / bars.Max, 0, 1);
                    pdf.Rect(Margin + labelWidth, y + 2, barWidth, 8, Zebra);
                    var color = bars.LowIsBad
                        ? (fraction < 0.99 ? Bad : fraction < 0.999 ? Warn : Good)
                        : (fraction > 0.9 ? Bad : fraction > 0.75 ? Warn : Accent);
                    pdf.Rect(Margin + labelWidth, y + 2, Math.Max(0.5, barWidth * fraction), 8, color);
                    pdf.TextRight(Margin + ContentWidth, y + 1, text, 8, bold: true);
                    y += rowHeight;
                }

                y += 8;
            }

            if (section.Table is { } table)
            {
                y = RenderTable(pdf, table, y, NewPage, () => y);
            }

            y += 14;
        }

        // Footers, now that the page count is known.
        var generated = doc.Subtitle;
        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SelectPage(i);
            pdf.Line(Margin, PdfWriter.PageHeight - 34, Margin + ContentWidth, PdfWriter.PageHeight - 34, Rule);
            pdf.Text(Margin, PdfWriter.PageHeight - 30, PdfWriter.Fit($"NetWatch · {doc.Title}", ContentWidth - 80, 7.5), 7.5, color: Muted);
            pdf.TextRight(Margin + ContentWidth, PdfWriter.PageHeight - 30, $"Page {i + 1} of {pdf.PageCount}", 7.5, color: Muted);
        }

        _ = generated;
        return pdf.ToBytes(doc.Title);
    }

    private static double RenderTable(PdfWriter pdf, ReportTable table, double startY, Action newPage, Func<double> currentY)
    {
        const double size = 7.5, rowHeight = 12, padding = 4;
        var y = startY;
        var columns = table.Headers.Count;
        var numeric = table.NumericColumns ?? new HashSet<int>();

        // Natural width per column (header and up to 200 rows), then scaled down to fit the page.
        var natural = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            natural[c] = PdfWriter.Width(table.Headers[c], size, bold: true);
            foreach (var row in table.Rows.Take(200))
            {
                natural[c] = Math.Max(natural[c], PdfWriter.Width(c < row.Length ? row[c] : string.Empty, size));
            }

            natural[c] = Math.Min(natural[c], 260) + (2 * padding);
        }

        var total = natural.Sum();
        var widths = natural.Select(w => total <= ContentWidth ? w * ContentWidth / total : w * ContentWidth / total).ToArray();

        void Header()
        {
            pdf.Rect(Margin, y, ContentWidth, rowHeight + 2, HeaderFill);
            var x = Margin;
            for (var c = 0; c < columns; c++)
            {
                var text = PdfWriter.Fit(table.Headers[c], widths[c] - (2 * padding), size, bold: true);
                if (numeric.Contains(c))
                {
                    pdf.TextRight(x + widths[c] - padding, y + 3, text, size, bold: true);
                }
                else
                {
                    pdf.Text(x + padding, y + 3, text, size, bold: true);
                }

                x += widths[c];
            }

            y += rowHeight + 2;
        }

        if (y + (3 * rowHeight) > Bottom)
        {
            newPage();
            y = Margin;
        }

        Header();
        if (table.Rows.Count == 0)
        {
            pdf.Text(Margin + padding, y + 3, table.Empty ?? "No data for this period.", size, color: Muted);
            return y + rowHeight + 4;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            if (y + rowHeight > Bottom)
            {
                newPage();
                y = Margin;
                Header();
            }

            if (r % 2 == 1)
            {
                pdf.Rect(Margin, y, ContentWidth, rowHeight, Zebra);
            }

            var row = table.Rows[r];
            var x = Margin;
            for (var c = 0; c < columns; c++)
            {
                var text = PdfWriter.Fit(c < row.Length ? row[c] : string.Empty, widths[c] - (2 * padding), size);
                if (numeric.Contains(c))
                {
                    pdf.TextRight(x + widths[c] - padding, y + 2.5, text, size);
                }
                else
                {
                    pdf.Text(x + padding, y + 2.5, text, size);
                }

                x += widths[c];
            }

            y += rowHeight;
        }

        pdf.Line(Margin, y, Margin + ContentWidth, y, Rule);
        _ = currentY;
        return y + 4;
    }
}

/// <summary>CSV output: every section's key figures and table, separated by a heading line. UTF-8 with BOM for Excel.</summary>
public static class CsvReportRenderer
{
    public static byte[] Render(ReportDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            void Row(params string[] cells)
            {
                foreach (var cell in cells)
                {
                    // Spreadsheet formula injection: values starting with = + - @ are prefixed with a quote.
                    csv.WriteField(cell.Length > 0 && "=+-@\t\r".Contains(cell[0]) && !double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "'" + cell : cell);
                }

                csv.NextRecord();
            }

            Row(doc.Title);
            Row(doc.Subtitle);
            foreach (var section in doc.Sections)
            {
                Row();
                Row($"## {section.Heading}");
                foreach (var (label, value) in section.Summary)
                {
                    Row(label, value);
                }

                if (section.Table is { } table)
                {
                    if (section.Summary.Count > 0)
                    {
                        Row();
                    }

                    Row([.. table.Headers]);
                    foreach (var r in table.Rows)
                    {
                        Row(r);
                    }
                }
                else if (section.Bars is { } bars)
                {
                    foreach (var (label, _, text) in bars.Items)
                    {
                        Row(label, text);
                    }
                }
            }
        }

        return stream.ToArray();
    }
}
