using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace NetWatch.Web.Reports;

/// <summary>
/// Minimal PDF 1.4 writer for reports: A4 pages, the standard Helvetica fonts (built into every PDF reader, so
/// nothing is embedded or downloaded), text, lines and filled rectangles. Text is WinAnsi (Windows-1252);
/// characters outside it print as '?'. Coordinates are in points from the top-left corner.
/// </summary>
public sealed class PdfWriter
{
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;

    private readonly List<StringBuilder> _pages = new();
    private StringBuilder? _page;

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
    }

    /// <summary>Selects a page already written (to add footers once the page count is known).</summary>
    public void SelectPage(int index) => _page = _pages[index];

    private StringBuilder Page => _page ?? throw new InvalidOperationException("Call NewPage first.");

    public void Text(double x, double y, string text, double size, bool bold = false, (double R, double G, double B)? color = null)
    {
        var c = color ?? (0.1, 0.1, 0.12);
        Page.Append(CultureInfo.InvariantCulture, $"BT {N(c.R)} {N(c.G)} {N(c.B)} rg /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y - size * 0.8)} Td (");
        AppendEscaped(Page, text);
        Page.Append(") Tj ET\n");
    }

    /// <summary>Right-aligned text ending at <paramref name="right"/>.</summary>
    public void TextRight(double right, double y, string text, double size, bool bold = false, (double R, double G, double B)? color = null) =>
        Text(right - Width(text, size, bold), y, text, size, bold, color);

    public void Rect(double x, double y, double w, double h, (double R, double G, double B) fill) =>
        Page.Append(CultureInfo.InvariantCulture, $"{N(fill.R)} {N(fill.G)} {N(fill.B)} rg {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f\n");

    public void Line(double x1, double y1, double x2, double y2, (double R, double G, double B) color, double width = 0.5) =>
        Page.Append(CultureInfo.InvariantCulture, $"{N(color.R)} {N(color.G)} {N(color.B)} RG {N(width)} w {N(x1)} {N(PageHeight - y1)} m {N(x2)} {N(PageHeight - y2)} l S\n");

    /// <summary>Width of <paramref name="text"/> in points (Helvetica metrics).</summary>
    public static double Width(string text, double size, bool bold = false)
    {
        var widths = bold ? BoldWidths : RegularWidths;
        double units = 0;
        foreach (var ch in text)
        {
            units += ch is >= ' ' and <= '~' ? widths[ch - ' '] : 556;
        }

        return units * size / 1000.0;
    }

    /// <summary>Cuts text to fit <paramref name="maxWidth"/>, ending with "…".</summary>
    public static string Fit(string text, double maxWidth, double size, bool bold = false)
    {
        if (Width(text, size, bold) <= maxWidth)
        {
            return text;
        }

        var ellipsis = Width("…", size, bold);
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (Width(sb.ToString() + ch, size, bold) + ellipsis > maxWidth)
            {
                break;
            }

            sb.Append(ch);
        }

        return sb.ToString().TrimEnd() + "…";
    }

    /// <summary>Splits text into lines that fit <paramref name="maxWidth"/> (word wrap).</summary>
    public static List<string> Wrap(string text, double maxWidth, double size, bool bold = false)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Width(candidate, size, bold) <= maxWidth || current.Length == 0)
            {
                current.Clear().Append(Width(candidate, size, bold) <= maxWidth ? candidate : Fit(candidate, maxWidth, size, bold));
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    public byte[] ToBytes(string title)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            output.Write(bytes);
        }

        void BeginObject(int id)
        {
            while (offsets.Count < id)
            {
                offsets.Add(0);
            }

            offsets[id - 1] = output.Position;
            Write($"{id} 0 obj\n");
        }

        // 1 catalog, 2 pages, 3 font regular, 4 font bold, 5 info, then (page, content) pairs.
        var pageCount = Math.Max(1, _pages.Count);
        var pageIds = Enumerable.Range(0, pageCount).Select(i => 6 + (i * 2)).ToList();

        Write("%PDF-1.4\n");
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // marks the file as binary
        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        Write($"<< /Type /Pages /Count {pageCount} /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] >>\nendobj\n");
        BeginObject(3);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObject(4);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObject(5);
        var info = new StringBuilder("<< /Producer (HIT SIEM) /Title (");
        AppendEscaped(info, title);
        info.Append(CultureInfo.InvariantCulture, $") /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>\nendobj\n");
        WriteWinAnsi(output, info.ToString());

        for (var i = 0; i < pageCount; i++)
        {
            var pageId = pageIds[i];
            BeginObject(pageId);
            Write(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageId + 1} 0 R >>\nendobj\n"));

            var content = i < _pages.Count ? _pages[i].ToString() : string.Empty;
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var raw = WinAnsi(content);
                    z.Write(raw, 0, raw.Length);
                }

                compressed = buffer.ToArray();
            }

            BeginObject(pageId + 1);
            Write($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            output.Write(compressed);
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AppendEscaped(StringBuilder sb, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\':
                case '(':
                case ')':
                    sb.Append('\\').Append(ch);
                    break;
                case '\r':
                case '\n':
                case '\t':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
    }

    private static void WriteWinAnsi(Stream output, string s)
    {
        var bytes = WinAnsi(s);
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Maps text to Windows-1252 bytes (the common typographic characters included).</summary>
    internal static byte[] WinAnsi(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            bytes[i] = ch switch
            {
                < (char)0x80 => (byte)ch,
                >= (char)0xA0 and <= (char)0xFF => (byte)ch,
                '€' => 0x80,
                '‚' => 0x82,
                '„' => 0x84,
                '…' => 0x85,
                '‘' => 0x91,
                '’' => 0x92,
                '“' => 0x93,
                '”' => 0x94,
                '•' => 0x95,
                '–' => 0x96,
                '—' => 0x97,
                '™' => 0x99,
                '→' => (byte)'>',
                '≥' => (byte)'>',
                '≤' => (byte)'<',
                _ => (byte)'?',
            };
        }

        return bytes;
    }

    // Helvetica / Helvetica-Bold advance widths (1/1000 em) for ' '..'~', from the standard AFM files.
    private static readonly short[] RegularWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] BoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];
}
