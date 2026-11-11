// IS4420WIN Current Loop Receiver - tests of the line parser, the line assembler and the port code.
// Lines: the current one (37 characters + CR LF, with the IS4420 temperature) and the one of
// earlier firmware, without the temperature (29 + CR LF).
//
// Build (Windows, from the project folder):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:LineTests.exe
//       tests\LineTests.cs src\Is4420Line.cs src\PortScanner.cs src\SerialLink.cs
// Run:
//   LineTests.exe                 parser and assembler tests
//   LineTests.exe scan            list the ports and what the scan hears on each
//   LineTests.exe link COM7 5     read COM7 for 5 s and count the lines

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Inacks.IS4420Monitor;

static class LineTests
{
    static int checks, failures;

    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok)
        {
            failures++;
            if (failures <= 40) Console.WriteLine("FAIL: " + what);
        }
    }

    // The IS4420-M1 line as the datasheet defines it: mA rounded to 0.01, % rounded to 0.1,
    // '-' only below 4 mA and never on zero, limits 99.99 mA and 127.9 %. The IS4420
    // temperature follows the %: a sign ('-' only below 0) and 3 digits, then "deg".
    static string Fields(long nA)
    {
        long ma100 = Math.Min((nA + 5000) / 10000, 9999);
        long d = nA - 4000000;
        long p10 = Math.Min((Math.Abs(d) + 8000) / 16000, 1279);
        char sign = (d < 0 && p10 != 0) ? '-' : '+';
        return String.Format(CultureInfo.InvariantCulture, "{0:00}.{1:00}mA_{2}{3:000}.{4}%",
            ma100 / 100, ma100 % 100, sign, p10 / 10, p10 % 10);
    }

    static string Temp(int t)
    {
        return (t < 0 ? "-" : "+") + Math.Abs(t).ToString("000", CultureInfo.InvariantCulture) + "deg";
    }

    static string FirmwareLine(long nA, int t)       // the current line, 37 characters
    {
        return Fields(nA) + "_" + Temp(t) + "_IS4420M1_ver0";
    }

    static string LegacyLine(long nA)                // earlier firmware, 29 characters
    {
        return Fields(nA) + "_IS4420M1_ver0";
    }

    static byte[] Bytes(string s)
    {
        byte[] b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    static List<RxLine> Feed(byte[] stream, int chunk, Random rnd)
    {
        LineAssembler a = new LineAssembler();
        List<RxLine> result = new List<RxLine>();
        int i = 0;
        while (i < stream.Length)
        {
            int n = chunk > 0 ? chunk : rnd.Next(1, 80);
            n = Math.Min(n, stream.Length - i);
            byte[] part = new byte[n + 5];
            Array.Copy(stream, i, part, 3, n);        // non-zero offset on purpose
            a.Feed(part, 3, n, i, DateTime.Now, result);
            i += n;
        }
        return result;
    }

    static string Key(RxLine l)
    {
        return l.Status + "|" + l.Text + "|" + l.Reason;
    }

    static bool Temperature(Is4420Reading r, int t)
    {
        return r != null && r.TemperatureKind == TemperatureKind.Value && r.Temperature == t;
    }

    static void TemperatureCases()
    {
        string err;
        // Examples of the current line.
        Is4420Reading r = Is4420Line.Parse("12.34mA_+052.1%_+025deg_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.Value && r.MilliAmps == 12.34m && r.Percent == 52.1m &&
              Temperature(r, 25) && r.Model == "IS4420M1" && r.Version == "ver0" && r.Warning == null, "12.34 mA, 25 C");
        r = Is4420Line.Parse("00.06mA_-024.6%_-040deg_IS4420M1_ver0", out err);
        Check(r != null && r.MilliAmps == 0.06m && r.Percent == -24.6m && Temperature(r, -40) && r.Warning == null, "0.06 mA, -40 C");
        r = Is4420Line.Parse("EOVERmA_+EOVER%_+025deg_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.OverRange && Temperature(r, 25), "over-range keeps the temperature");
        r = Is4420Line.Parse("ERRORmA_+ERROR%_+ERRdeg_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.NoMeasurement && r.TemperatureKind == TemperatureKind.NoMeasurement, "ERROR line with +ERR");
        r = Is4420Line.Parse("04.00mA_+000.0%_+000deg_IS4420M1_ver0", out err);
        Check(Temperature(r, 0) && r.Warning == null, "0 C is +000");
        r = Is4420Line.Parse("04.00mA_+000.0%_+125deg_IS4420M1_ver0", out err);
        Check(Temperature(r, 125), "+125 C");
        r = Is4420Line.Parse("04.00mA_+000.0%_-125deg_IS4420M1_ver0", out err);
        Check(Temperature(r, -125), "-125 C");
        r = Is4420Line.Parse("04.00mA_+000.0%_-001deg_IS4420M1_ver0", out err);
        Check(Temperature(r, -1) && r.Warning == null, "-1 C");
        r = Is4420Line.Parse("04.00mA_+000.0%_+126deg_IS4420M1_ver0", out err);
        Check(Temperature(r, 126), "+126 (impossible register value, still a well-formed line) is shown");
        // Combinations the firmware does not send, read as they are.
        r = Is4420Line.Parse("12.34mA_+052.1%_+ERRdeg_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.Value && r.TemperatureKind == TemperatureKind.NoMeasurement, "a value with +ERR");
        r = Is4420Line.Parse("ERRORmA_+ERROR%_+025deg_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.NoMeasurement && Temperature(r, 25), "ERROR with a temperature");
        // Warnings.
        r = Is4420Line.Parse("04.00mA_+000.0%_-000deg_IS4420M1_ver0", out err);
        Check(Temperature(r, 0) && r.Warning == "negative zero temperature", "-000 is flagged and read as 0");
        r = Is4420Line.Parse("12.34mA_+060.0%_-000deg_IS4420M1_ver0", out err);
        Check(r != null && r.Warning != null && r.Warning.Contains("disagree") && r.Warning.Contains("negative zero temperature"), "two warnings in one line");
        // Lines without the temperature (earlier firmware).
        r = Is4420Line.Parse("12.34mA_+052.1%_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.Value && r.TemperatureKind == TemperatureKind.None && r.MilliAmps == 12.34m, "29-character line: no temperature");
        r = Is4420Line.Parse("EOVERmA_+EOVER%_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.OverRange && r.TemperatureKind == TemperatureKind.None, "29-character EOVER line");

        string[] bad =
        {
            "12.34mA_+052.1%_-ERRdeg_IS4420M1_ver0",    // the flag always carries '+'
            "12.34mA_+052.1%_ 025deg_IS4420M1_ver0",    // space instead of the sign
            "12.34mA_+052.1%_+025DEG_IS4420M1_ver0",    // upper-case unit
            "12.34mA_+052.1%_+025dgr_IS4420M1_ver0",
            "12.34mA_+052.1%_+025deg-IS4420M1_ver0",
            "12.34mA_+052.1%_+0x5deg_IS4420M1_ver0",
            "12.34mA_+052.1%_+err_deg_S4420M1_ver0",
            "12.34mA_+052.1%_+25deg_IS4420M1_ver0",     // 2 digits: 36 characters
            "12.34mA_+052.1%_+0025deg_IS4420M1_ver0",   // 4 digits: 38 characters
            "12.34mA_+052.1%_IS4420M1IS4420M1_ver0",    // 37 characters without a temperature
            "12.34mA_+052.1%_+025deg__ver0",            // 29 characters with a temperature
            "12.34mA_+052.1%_+025deg_IS4420M1_ver ",
            "EOVERmA_+052.1%_+025deg_IS4420M1_ver0",
        };
        foreach (string b in bad)
        {
            Check(Is4420Line.Parse(b, out err) == null && err != null, "rejected: " + b);
        }
    }

    static void ParserCases()
    {
        string err;
        Is4420Reading r = Is4420Line.Parse("12.34mA_+052.1%_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.Value && r.MilliAmps == 12.34m && r.Percent == 52.1m &&
              r.Model == "IS4420M1" && r.Version == "ver0" && r.Warning == null, "12.34 mA example");

        r = Is4420Line.Parse("04.00mA_+000.0%_IS4420M1_ver0", out err);
        Check(r != null && r.MilliAmps == 4m && r.Percent == 0m && r.Warning == null, "4.00 mA");
        r = Is4420Line.Parse("03.99mA_-000.1%_IS4420M1_ver0", out err);
        Check(r != null && r.MilliAmps == 3.99m && r.Percent == -0.1m && r.Warning == null, "3.99 mA, -0.1 %");
        r = Is4420Line.Parse("00.06mA_-024.6%_IS4420M1_ver0", out err);
        Check(r != null && r.MilliAmps == 0.06m && r.Percent == -24.6m && r.Warning == null, "0.06 mA example");
        r = Is4420Line.Parse("21.60mA_+110.0%_IS4420M1_ver0", out err);
        Check(r != null && r.MilliAmps == 21.6m && r.Percent == 110m && r.Warning == null, "21.60 mA");
        r = Is4420Line.Parse("EOVERmA_+EOVER%_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.OverRange, "EOVER line");
        r = Is4420Line.Parse("ERRORmA_+ERROR%_IS4420M1_ver0", out err);
        Check(r != null && r.Kind == ReadingKind.NoMeasurement, "ERROR line");
        r = Is4420Line.Parse("12.34mA_+052.1%_IS4420M1_ver7", out err);
        Check(r != null && r.Version == "ver7", "other MODULE_REV digit");
        r = Is4420Line.Parse("12.34mA_+052.1%_IS4421M2_ver1", out err);
        Check(r != null && r.Model == "IS4421M2" && r.Version == "ver1", "other model token of the same length");

        string[] bad =
        {
            "EOVERmA_+052.1%_IS4420M1_ver0",   // flag and number mixed
            "12.34mA_+EOVER%_IS4420M1_ver0",
            "ERRORmA_+EOVER%_IS4420M1_ver0",
            "EOVERmA_-EOVER%_IS4420M1_ver0",   // flags always carry '+'
            "12,34mA_+052.1%_IS4420M1_ver0",   // comma
            "12.34ma_+052.1%_IS4420M1_ver0",   // lower-case unit
            "12.34mA_ 052.1%_IS4420M1_ver0",   // space instead of the sign
            "12.34mA_+052.1%-IS4420M1_ver0",
            "12.34mA_+052.1%_IS4420M1_ver ",
            "12.34mA_+52.10%_IS4420M1_ver0",
            "1.234mA_+052.1%_IS4420M1_ver0",
        };
        foreach (string b in bad)
        {
            Check(Is4420Line.Parse(b, out err) == null && err != null, "rejected: " + b);
        }

        Check(Is4420Line.IsOldFormat("START_12.34mA_+052.1%_IS4420M1_ver0_END"), "old format recognised");
        Check(!Is4420Line.IsOldFormat("START_12.34mA_+052.1%_IS4420M1_ver0_EN"), "short old line not recognised");
        Check(!Is4420Line.IsOldFormat("START_12.34mA_+052.1%_IS4420M1_ver0END_"), "malformed old line not recognised");
        Check(!Is4420Line.IsOldFormat("12.34mA_+052.1%_IS4420M1_ver0"), "new line is not the old format");

        r = Is4420Line.Parse("12.34mA_+060.0%_IS4420M1_ver0", out err);
        Check(r != null && r.Warning != null && r.Warning.Contains("disagree"), "mA/% disagreement is flagged");
        r = Is4420Line.Parse("04.00mA_-000.0%_IS4420M1_ver0", out err);
        Check(r != null && r.Warning == "negative zero percentage" && r.Percent == 0m &&
              r.Percent.ToString("0.0", CultureInfo.InvariantCulture) == "0.0", "-000.0 is flagged and shown as 0.0");
    }

    static void AssemblerCases()
    {
        const string L = "12.34mA_+052.1%_+025deg_IS4420M1_ver0\r\n";
        const string LL = "12.34mA_+052.1%_IS4420M1_ver0\r\n";
        Random rnd = new Random(1);

        // Port opened in the middle of a line: the fragment is Sync, not Rejected.
        List<RxLine> lines = Feed(Bytes(L.Substring(9) + L + L + L), 0, rnd);
        Check(lines.Count == 4 && lines[0].Status == LineStatus.Sync && lines[1].Status == LineStatus.Valid &&
              lines[3].Status == LineStatus.Valid, "start mid-line: 1 sync + 3 valid");

        // Port opened exactly at a line start: the first line is kept.
        lines = Feed(Bytes(L + L), 0, rnd);
        Check(lines.Count == 2 && lines[0].Status == LineStatus.Valid && lines[1].Status == LineStatus.Valid,
              "start at a line boundary: first line valid");

        // After the first line, every bad line counts as rejected, with the reason.
        string stream = L +
            "\u0000" + L +                                         // stray byte at power-up
            "12.34mA_+052.1%_+025deg_IS4420M1_ver0\n" +            // LF without CR
            "START_12.34mA_+052.1%_IS4420M1_ver0_END\r\n" +        // the old START_..._END line, 41 bytes
            "\r\n" +                                               // empty line
            "12.34mA_+052.1%_+025deg_IS4420M1_ver\u00FF\r\n" +     // non-printable byte
            "12.34mA_+052.1%_+025deg_IS4420M1_ve\r\n" +            // 35 characters
            "\u0000" + LL +                                        // stray byte before a 29-character line
            LL +                                                   // earlier line without the temperature
            L;
        lines = Feed(Bytes(stream), 0, rnd);
        Check(lines.Count == 10, "10 lines in the mixed stream (got " + lines.Count + ")");
        if (lines.Count == 10)
        {
            Check(lines[0].Status == LineStatus.Valid && lines[0].Reading.Temperature == 25, "mixed: line 1 valid, 25 C");
            Check(lines[1].Status == LineStatus.Rejected && lines[1].Reason == "38 characters before CR LF, 37 expected" && lines[1].Text.StartsWith("<00>12.34"), "stray byte: rejected, 38 characters, shown as <00>");
            Check(lines[2].Status == LineStatus.Rejected && lines[2].Reason == "LF without CR", "LF without CR");
            Check(lines[3].Status == LineStatus.Rejected && lines[3].OldFormat && lines[3].Reason.StartsWith("old line format"), "old START_..._END line rejected and recognised as the old format");
            Check(lines[4].Status == LineStatus.Rejected && lines[4].Reason.StartsWith("0 characters"), "empty line rejected");
            Check(lines[5].Status == LineStatus.Rejected && lines[5].Text.EndsWith("<FF>") && lines[5].Reason == "byte that is not printable text", "non-printable byte rejected and shown as <FF>");
            Check(lines[6].Status == LineStatus.Rejected && lines[6].Reason.StartsWith("35 characters"), "35 characters rejected");
            Check(lines[7].Status == LineStatus.Rejected && lines[7].Reason.StartsWith("30 characters"), "stray byte before a 29-character line: 30 characters, rejected");
            Check(lines[8].Status == LineStatus.Valid && lines[8].Reading.TemperatureKind == TemperatureKind.None, "29-character line valid, without temperature");
            Check(lines[9].Status == LineStatus.Valid && lines[9].Reading.TemperatureKind == TemperatureKind.Value, "valid again after the bad lines");
        }

        // Noise without LF: dropped in blocks of 256 bytes.
        StringBuilder noise = new StringBuilder();
        for (int i = 0; i < 600; i++) noise.Append((char)('A' + i % 26));
        lines = Feed(Bytes(L + noise + "\r\n" + L), 0, rnd);
        int rejected = 0, valid = 0;
        foreach (RxLine l in lines)
        {
            if (l.Status == LineStatus.Rejected) rejected++;
            if (l.Status == LineStatus.Valid) valid++;
        }
        Check(valid == 2 && rejected == 3, "600 bytes of noise: 2 overflow blocks + the remainder rejected, lines around it valid");

        // Same result whatever the chunk size.
        StringBuilder big = new StringBuilder(stream);
        for (int k = 0; k < 50; k++) big.Append(FirmwareLine(k * 437123L % 24000000L, k * 7 % 251 - 125)).Append("\r\n");
        for (int k = 0; k < 10; k++) big.Append(LegacyLine(k * 1234567L % 24000000L)).Append("\r\n");
        big.Append("EOVERmA_+EOVER%_+031deg_IS4420M1_ver0\r\nERRORmA_+ERROR%_+ERRdeg_IS4420M1_ver0\r\n").Append(noise).Append("\r\n");
        byte[] bigBytes = Bytes("+025deg_IS4420M1_ver0\r\n" + big);
        List<RxLine> reference = Feed(bigBytes, bigBytes.Length, rnd);
        bool same = true;
        for (int chunk = 0; chunk <= 64 && same; chunk++)
        {
            for (int rep = 0; rep < (chunk == 0 ? 20 : 1); rep++)
            {
                List<RxLine> other = Feed(bigBytes, chunk, rnd);
                if (other.Count != reference.Count) { same = false; break; }
                for (int i = 0; i < other.Count; i++) if (Key(other[i]) != Key(reference[i])) { same = false; break; }
            }
        }
        Check(same, "identical lines for every chunk size 1..64 and random chunks");
    }

    static void FirmwareSweep(string cultureName)
    {
        CultureInfo old = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(cultureName);
        try
        {
            List<long> currents = new List<long>();
            for (long nA = 0; nA <= 24400000; nA += 1000) currents.Add(nA);
            for (long nA = 3980000; nA <= 4020000; nA++) currents.Add(nA);
            Random rnd = new Random(7);
            for (int i = 0; i < 20000; i++) currents.Add((long)(rnd.NextDouble() * 24400000));

            // Every temperature from -125 to +125 C, in turn; every 7th line from the older
            // firmware without the temperature.
            StringBuilder sb = new StringBuilder(currents.Count * 39);
            int legacyCount = 0;
            for (int i = 0; i < currents.Count; i++)
            {
                if (i % 7 == 3)
                {
                    sb.Append(LegacyLine(currents[i])).Append("\r\n");
                    legacyCount++;
                }
                else
                {
                    sb.Append(FirmwareLine(currents[i], i % 251 - 125)).Append("\r\n");
                }
            }
            byte[] stream = Bytes(sb.ToString());
            Check(stream.Length == (currents.Count - legacyCount) * 39 + legacyCount * 31, "every generated line is 39 bytes (31 without the temperature)");

            List<RxLine> lines = Feed(stream, 0, rnd);
            int bad = 0;
            for (int i = 0; i < currents.Count && i < lines.Count; i++)
            {
                long nA = currents[i];
                long ma100 = Math.Min((nA + 5000) / 10000, 9999);
                long d = nA - 4000000;
                long p10 = Math.Min((Math.Abs(d) + 8000) / 16000, 1279);
                decimal ma = ma100 / 100m;
                decimal pct = (d < 0 ? -p10 : p10) / 10m;
                RxLine l = lines[i];
                bool legacy = i % 7 == 3;
                bool tempOk = l.Reading != null && (legacy
                    ? l.Reading.TemperatureKind == TemperatureKind.None
                    : l.Reading.TemperatureKind == TemperatureKind.Value && l.Reading.Temperature == i % 251 - 125);
                if (l.Status != LineStatus.Valid || l.Reading.Kind != ReadingKind.Value || !tempOk ||
                    l.Reading.MilliAmps != ma || l.Reading.Percent != pct || l.Reading.Warning != null)
                {
                    if (bad < 5) Console.WriteLine("  sweep " + cultureName + ": " + l.Text + " " + l.Status + " " + l.Reason + " " + (l.Reading != null ? l.Reading.Warning : ""));
                    bad++;
                }
            }
            Check(lines.Count == currents.Count && bad == 0,
                cultureName + ": " + currents.Count + " firmware lines from 0 to 24.4 mA and -125 to 125 C all valid, exact, no warning (bad " + bad + ")");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
            Thread.CurrentThread.CurrentUICulture = old;
        }
    }

    static void PortNameCases()
    {
        List<string> names = new List<string>(new string[] { "COM10", "COM2", "COM1", "COM100", "COM3" });
        names.Sort(PortScanner.NaturalCompare);
        Check(String.Join(",", names.ToArray()) == "COM1,COM2,COM3,COM10,COM100", "natural port order");
    }

    static int RunScan()
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<PortInfo> ports = PortScanner.Scan(null);
        Console.WriteLine("scan took " + sw.ElapsedMilliseconds + " ms");
        foreach (PortInfo p in ports) Console.WriteLine("  " + p.ToString() + "   [" + p.Probe + (p.Detail != null ? ": " + p.Detail : "") + "]");
        return 0;
    }

    static int RunLink(string port, double seconds)
    {
        ConcurrentQueue<LinkEvent> q = new ConcurrentQueue<LinkEvent>();
        SerialLink link = new SerialLink(q);
        link.Start(port);
        Stopwatch sw = Stopwatch.StartNew();
        int valid = 0, rejected = 0, sync = 0;
        long first = 0, last = 0;
        Is4420Reading lastReading = null;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            LinkEvent ev;
            while (q.TryDequeue(out ev))
            {
                if (ev.Kind == LinkEventKind.Data)
                {
                    foreach (RxLine l in ev.Lines)
                    {
                        if (l.Status == LineStatus.Valid)
                        {
                            valid++;
                            if (first == 0) first = l.Timestamp;
                            last = l.Timestamp;
                            lastReading = l.Reading;
                        }
                        else if (l.Status == LineStatus.Rejected) rejected++;
                        else sync++;
                    }
                }
                else
                {
                    Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "  {0,6:0.000} s  {1} {2}", sw.Elapsed.TotalSeconds, ev.Kind, ev.Message));
                }
            }
            Thread.Sleep(20);
        }
        link.Stop();
        double period = valid > 1 ? (last - first) * 1000.0 / Stopwatch.Frequency / (valid - 1) : 0;
        Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "valid {0}, rejected {1}, sync {2}, average period {3:0.0} ms, last {4}",
            valid, rejected, sync, period,
            lastReading == null ? "-" : lastReading.Kind + " " + lastReading.MilliAmps + " mA " + lastReading.Percent + " %"));
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "scan") return RunScan();
        if (args.Length >= 3 && args[0] == "link") return RunLink(args[1], double.Parse(args[2], CultureInfo.InvariantCulture));

        ParserCases();
        TemperatureCases();
        AssemblerCases();
        PortNameCases();
        FirmwareSweep("en-US");
        FirmwareSweep("es-ES");
        FirmwareSweep("de-DE");
        Console.WriteLine(checks + " checks, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
