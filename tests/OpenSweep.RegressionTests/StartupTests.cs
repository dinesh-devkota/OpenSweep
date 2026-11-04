using OpenSweep.Core;

internal static partial class Program
{
    static async Task StartupTests()
    {
        await Run("Startup approval recognizes supported enabled and disabled states", StartupSupportedStates);
        await Run("Startup approval keeps malformed and future flags unknown", StartupUnknownStates);
        await Run("Startup approval distinguishes absent values from failed reads", StartupReadFailure);
        await Run("Startup search matches name publisher and command", StartupSearch);
        await Run("Startup state filters preserve unknown status", StartupStateFilter);
        await Run("Startup CSV escapes commas quotes and multiline values", StartupCsvEscaping);
        await Run("Startup CSV neutralizes spreadsheet formulas in every text column", StartupCsvFormulas);
        await Run("Startup CSV handles empty and null data explicitly", StartupCsvEmpty);
    }

    static byte[] ApprovalBytes(uint flags, int length = 12)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)flags;
        bytes[1] = (byte)(flags >> 8);
        bytes[2] = (byte)(flags >> 16);
        bytes[3] = (byte)(flags >> 24);
        return bytes;
    }

    static Task StartupSupportedStates() => Sync(() =>
    {
        Equal(StartupState.Enabled, StartupApproval.Parse(ApprovalBytes(2)));
        Equal(StartupState.Enabled, StartupApproval.Parse(ApprovalBytes(6)));
        Equal(StartupState.Disabled, StartupApproval.Parse(ApprovalBytes(3)));
        Equal(StartupState.Disabled, StartupApproval.Parse(ApprovalBytes(7)));
        var timestamp = ApprovalBytes(3);
        for (int i = 4; i < timestamp.Length; i++) timestamp[i] = 0xff;
        Equal(StartupState.Disabled, StartupApproval.Parse(timestamp));
    });

    static Task StartupUnknownStates() => Sync(() =>
    {
        foreach (var malformed in new object[] { 2, "Enabled", new byte[0], new byte[] { 2 }, ApprovalBytes(2, 11) })
            Equal(StartupState.Unknown, StartupApproval.Parse(malformed));
        foreach (uint flag in new uint[] { 0, 1, 4, 5, 8, 0x102, 0xffffffff })
            Equal(StartupState.Unknown, StartupApproval.Parse(ApprovalBytes(flag)));
    });

    static Task StartupReadFailure() => Sync(() =>
    {
        Equal(StartupState.Enabled, StartupApproval.Parse(null));
        Equal(StartupState.Unknown, StartupApproval.Parse(null, false));
        Equal(StartupState.Unknown, StartupApproval.Parse(ApprovalBytes(2), false));
    });

    static Task StartupSearch() => Sync(() =>
    {
        const string name = "Example Drive", publisher = "Müller Software", command = @"C:\Program Files\Example\drive.exe --background";
        foreach (string query in new[] { "  DRIVE  ", "Müller", "--BACKGROUND", "", " " })
            Check(StartupFilter.Matches(name, publisher, command, StartupState.Enabled, query, null), "Expected search match: " + query);
        Check(StartupFilter.Matches(name, publisher, command, StartupState.Enabled, null, null), "Null search should include all entries");
        Check(!StartupFilter.Matches(name, publisher, command, StartupState.Enabled, "unrelated", null), "Unrelated search matched");
        Check(!StartupFilter.Matches(null, null, null, StartupState.Unknown, "drive", null), "Null fields matched a nonempty search");
    });

    static Task StartupStateFilter() => Sync(() =>
    {
        Check(StartupFilter.Matches("Example", null, null, StartupState.Unknown, "example", StartupState.Unknown), "Unknown entries cannot be filtered explicitly");
        Check(!StartupFilter.Matches("Example", null, null, StartupState.Unknown, "example", StartupState.Enabled), "Unknown state was treated as enabled");
        Check(!StartupFilter.Matches("Example", null, null, StartupState.Disabled, "", StartupState.Enabled), "Disabled entry matched enabled filter");
        Check(StartupFilter.Matches("Example", null, null, StartupState.Disabled, "", null), "All-status filter dropped disabled entry");
        Check(!StartupFilter.Matches("Example", null, null, StartupState.Enabled, "missing", StartupState.Enabled), "Matching state bypassed search");
    });

    static Task StartupCsvEscaping() => Sync(() =>
    {
        var report = StartupReport.Csv(new[] { new StartupReportRow
        {
            Name = "Example, \"Desktop\"", Publisher = "Müller 日本語", State = StartupState.Disabled,
            Location = "Current user", Command = "first line\r\nsecond line", FilePath = @"C:\Apps\Example.exe"
        } });
        Equal("Name,Publisher,Status,Location,Command,Executable\r\n" +
            "\"Example, \"\"Desktop\"\"\",\"Müller 日本語\",\"Disabled\",\"Current user\",\"first line\r\nsecond line\",\"C:\\Apps\\Example.exe\"\r\n", report);
    });

    static Task StartupCsvFormulas() => Sync(() =>
    {
        foreach (string value in new[] { "=1+1", "+SUM(A1:A2)", "-1+2", "@SUM(A1:A2)", "  =1", "\uFEFF@SUM(A1:A2)", "\ttext", "\rtext", "\ntext", " \t-1", "\0=1", "\u0001text", "\uFEFF\0@1" })
        {
            var text = StartupReport.Csv(new[] { new StartupReportRow
            {
                Name = value, Publisher = value, State = StartupState.Unknown,
                Location = value, Command = value, FilePath = value
            } });
            var rows = ParseCsv(text);
            Equal(2, rows.Count); Equal(6, rows[1].Count);
            foreach (int column in new[] { 0, 1, 3, 4, 5 })
                Equal("'" + value, rows[1][column]);
            Equal("Unknown", rows[1][2]);
        }
        var safe = ParseCsv(StartupReport.Csv(new[] { new StartupReportRow { Name = "Example.exe", Command = @"C:\Example.exe --start" } }));
        Equal("Example.exe", safe[1][0]); Equal(@"C:\Example.exe --start", safe[1][4]);
    });

    static Task StartupCsvEmpty() => Sync(() =>
    {
        Equal("Name,Publisher,Status,Location,Command,Executable\r\n", StartupReport.Csv(Array.Empty<StartupReportRow>()));
        var rows = ParseCsv(StartupReport.Csv(new[] { new StartupReportRow() }));
        Equal(6, rows[1].Count);
        foreach (int column in new[] { 0, 1, 3, 4, 5 }) Equal("", rows[1][column]);
        Throws<ArgumentNullException>(() => StartupReport.Csv(null));
        Throws<ArgumentException>(() => StartupReport.Csv(new StartupReportRow[] { null }));
    });

    // A small independent CSV reader verifies field boundaries and values after quoting/escaping.
    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (!quoted && (c == '\r' || c == '\n'))
            {
                row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>();
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else field.Append(c);
        }
        Check(!quoted, "CSV ended inside a quoted field");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
