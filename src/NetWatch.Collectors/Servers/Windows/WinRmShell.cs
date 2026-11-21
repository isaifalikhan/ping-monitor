using System.Text;
using System.Xml.Linq;

namespace NetWatch.Collectors.Servers.Windows;

/// <summary>Pure helpers for the WS-Management Shell protocol (unit tested with a captured ReceiveResponse).</summary>
public static class WinRmShell
{
    /// <summary>
    /// Appends the base64 stdout/stderr chunks of a ReceiveResponse to <paramref name="output"/> and returns true
    /// when the response says the command is done.
    /// </summary>
    public static bool ParseReceive(string responseXml, StringBuilder output)
    {
        var doc = XDocument.Parse(responseXml);
        foreach (var stream in doc.Descendants().Where(e => e.Name.LocalName == "Stream"))
        {
            if (!string.IsNullOrEmpty(stream.Value))
            {
                output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(stream.Value.Trim())));
            }
        }

        return doc.Descendants().Any(e => e.Name.LocalName == "CommandState" &&
                                           ((string?)e.Attribute("State"))?.EndsWith("/CommandState/Done", StringComparison.Ordinal) == true);
    }
}
