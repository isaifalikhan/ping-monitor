using System.Globalization;
using NetWatch.Core.Configuration;

namespace NetWatch.Web.Api;

/// <summary>CSV downloads of search results (syslog, traps, Windows events, alerts): UTF-8 with BOM, PKT times, formula-injection safe.</summary>
internal static class CsvExport
{
    /// <summary>Maximum rows of one export (newest first), the same limit as the DC security-event export.</summary>
    public const int MaxRows = DcEndpoints.MaxExportRows;

    public static byte[] Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        using (var csv = new CsvHelper.CsvWriter(writer, new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            foreach (var h in headers)
            {
                csv.WriteField(h);
            }

            csv.NextRecord();
            foreach (var row in rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(Reports.CsvReportRenderer.SafeCell(cell));
                }

                csv.NextRecord();
            }
        }

        return stream.ToArray();
    }

    public static string Time(DateTime? utc) => utc is { } t ? LocalTime.ToLocal(t).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    public static string FileName(string prefix) => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
}
