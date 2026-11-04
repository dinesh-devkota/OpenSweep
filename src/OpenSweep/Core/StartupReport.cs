using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenSweep.Core
{
    public enum StartupState { Unknown, Enabled, Disabled }

    /// <summary>Windows StartupApproved flags. An absent value uses Windows' enabled default.</summary>
    public static class StartupApproval
    {
        public static StartupState Parse(object value, bool readSucceeded = true)
        {
            if (!readSucceeded) return StartupState.Unknown;
            if (value == null) return StartupState.Enabled;
            if (!(value is byte[] bytes) || bytes.Length < 12) return StartupState.Unknown;
            // The first DWORD stores approval; the remaining bytes hold the change timestamp.
            uint flag = (uint)bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
            if (flag == 2 || flag == 6) return StartupState.Enabled;
            if (flag == 3 || flag == 7) return StartupState.Disabled;
            return StartupState.Unknown;
        }
    }

    public static class StartupFilter
    {
        public static bool Matches(string name, string publisher, string command, StartupState state,
            string query, StartupState? filter)
        {
            if (filter.HasValue && state != filter.Value) return false;
            query = (query ?? "").Trim();
            if (query.Length == 0) return true;
            return new[] { name, publisher, command }.Any(value =>
                (value ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }
    }

    public sealed class StartupReportRow
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public StartupState State { get; set; }
        public string Location { get; set; }
        public string Command { get; set; }
        public string FilePath { get; set; }
    }

    public static class StartupReport
    {
        public static string Csv(IEnumerable<StartupReportRow> rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            var text = new StringBuilder("Name,Publisher,Status,Location,Command,Executable\r\n");
            foreach (var row in rows)
            {
                if (row == null) throw new ArgumentException("Report rows cannot be null.", nameof(rows));
                text.Append(string.Join(",", new[] { row.Name, row.Publisher, row.State.ToString(),
                    row.Location, row.Command, row.FilePath }.Select(CsvReport.EncodeCell))).Append("\r\n");
            }
            return text.ToString();
        }

    }

    internal static class CsvReport
    {
        internal static string EncodeCell(string value)
        {
            value = value ?? "";
            int first = 0;
            while (first < value.Length && (char.IsWhiteSpace(value[first]) || char.IsControl(value[first]) || value[first] == '\uFEFF')) first++;
            // Registry values and shortcut names are untrusted spreadsheet input, even inside quoted CSV cells.
            if ((first < value.Length && "=+-@".IndexOf(value[first]) >= 0) ||
                (value.Length > 0 && char.IsControl(value[0])))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
