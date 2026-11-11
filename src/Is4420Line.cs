// IS4420WIN Current Loop Receiver - line parser and line assembler.
//
// The IS4420-M1 sends one line per measurement, about every 103 ms, 115200 8N1.
// Current firmware (39 bytes, 37 characters before CR LF):
//     xx.xxmA_+xxx.x%_+xxxdeg_IS4420M1_ver0 CR LF    (current, percentage, temperature)
//     EOVERmA_+EOVER%_+xxxdeg_IS4420M1_ver0 CR LF    (over-range; the temperature is still sent)
//     ERRORmA_+ERROR%_+ERRdeg_IS4420M1_ver0 CR LF    (no measurement)
// Earlier firmware, without the temperature (31 bytes, 29 characters before CR LF):
//     xx.xxmA_+xxx.x%_IS4420M1_ver0 CR LF
// The % sign is '-' only below 4 mA and the temperature sign only below 0 C; zero is always '+'.
// Reading rule (datasheet): keep a line only if exactly 37 characters come before its CR LF.
// This program also keeps 29-character lines, so modules with earlier firmware still work.
//
// This file has no Windows dependency, so the same code runs in the unit tests.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Inacks.IS4420Monitor
{
    public enum ReadingKind { Value, OverRange, NoMeasurement }

    // None: the line has no temperature field (earlier firmware).
    public enum TemperatureKind { None, Value, NoMeasurement }

    public sealed class Is4420Reading
    {
        public ReadingKind Kind;
        public decimal MilliAmps;   // only for ReadingKind.Value
        public decimal Percent;     // only for ReadingKind.Value, negative below 4 mA
        public TemperatureKind TemperatureKind;
        public int Temperature;     // whole degrees C, only for TemperatureKind.Value
        public string Model;        // for example "IS4420M1"
        public string Version;      // for example "ver0"
        public string Warning;      // null when the fields agree with each other
    }

    public enum LineStatus { Valid, Rejected, Sync }

    public sealed class RxLine
    {
        public LineStatus Status;
        public string Text;            // the bytes before CR LF, printable; other bytes as <XX>
        public string Reason;          // why the line was not accepted (null when valid)
        public Is4420Reading Reading;  // only when Status == Valid
        public bool OldFormat;         // START_ + line + _END, the 41-byte line used before
        public long Timestamp;         // Stopwatch ticks when the LF arrived
        public DateTime WallTime;      // PC clock when the LF arrived
    }

    public static class Is4420Line
    {
        public const int ContentLength = 37;               // characters before CR LF
        public const int ContentLengthNoTemperature = 29;  // earlier firmware, no temperature
        public const int MaxLineBytes = 256;   // a longer run without LF is reported and dropped

        // mA and % are each 1 mA = 6.25 % apart; both fields are rounded from the same value,
        // so they never disagree by more than 0.09 % (0.16 % if the module truncated).
        public const decimal PercentTolerance = 0.2m;

        static readonly Regex Pattern = new Regex(
            "^(?<ma>[0-9]{2}\\.[0-9]{2}|EOVER|ERROR)mA_(?<sign>[+-])(?<pct>[0-9]{3}\\.[0-9]|EOVER|ERROR)%_" +
            "(?:(?<tsign>[+-])(?<temp>[0-9]{3}|ERR)deg_)?(?<model>[0-9A-Za-z]{8})_(?<ver>ver[0-9])$",
            RegexOptions.CultureInvariant);

        // Parses the 37 (or 29) characters before CR LF. Returns null and an error text when
        // the line is not in the IS4420 format.
        public static Is4420Reading Parse(string content, out string error)
        {
            error = null;
            if (content == null || (content.Length != ContentLength && content.Length != ContentLengthNoTemperature))
            {
                error = "not 37 characters";
                return null;
            }
            Match m = Pattern.Match(content);
            if (!m.Success)
            {
                error = "not in the IS4420 line format";
                return null;
            }
            string ma = m.Groups["ma"].Value;
            string sign = m.Groups["sign"].Value;
            string pct = m.Groups["pct"].Value;

            Is4420Reading r = new Is4420Reading();
            r.Model = m.Groups["model"].Value;
            r.Version = m.Groups["ver"].Value;
            if (!ParseTemperature(m, r, out error)) return null;

            bool maIsNumber = ma[0] >= '0' && ma[0] <= '9';
            bool pctIsNumber = pct[0] >= '0' && pct[0] <= '9';
            if (maIsNumber && pctIsNumber)
            {
                r.Kind = ReadingKind.Value;
                r.MilliAmps = decimal.Parse(ma, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                decimal magnitude = decimal.Parse(pct, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                r.Percent = (sign == "-" && magnitude != 0m) ? -magnitude : magnitude;
                if (sign == "-" && magnitude == 0m)
                {
                    AddWarning(r, "negative zero percentage");
                }
                else
                {
                    decimal fromMilliAmps = (r.MilliAmps - 4m) * 6.25m;
                    if (Math.Abs(r.Percent - fromMilliAmps) > PercentTolerance)
                    {
                        AddWarning(r, String.Format(CultureInfo.InvariantCulture,
                            "mA and % disagree: {0:0.00} mA is {1:0.00} %", r.MilliAmps, fromMilliAmps));
                    }
                }
                return r;
            }
            if (ma == "EOVER" && pct == "EOVER" && sign == "+")
            {
                r.Kind = ReadingKind.OverRange;
                return r;
            }
            if (ma == "ERROR" && pct == "ERROR" && sign == "+")
            {
                r.Kind = ReadingKind.NoMeasurement;
                return r;
            }
            error = "mA and % fields do not match (number and flag mixed)";
            return null;
        }

        // The temperature field: absent (earlier firmware), +ERR, or a sign and 3 digits.
        static bool ParseTemperature(Match m, Is4420Reading r, out string error)
        {
            error = null;
            Group tsign = m.Groups["tsign"];
            if (!tsign.Success)
            {
                r.TemperatureKind = TemperatureKind.None;
                return true;
            }
            string temp = m.Groups["temp"].Value;
            if (temp == "ERR")
            {
                if (tsign.Value != "+")
                {
                    error = "temperature flag with a '-' sign";
                    return false;
                }
                r.TemperatureKind = TemperatureKind.NoMeasurement;
                return true;
            }
            int magnitude = int.Parse(temp, NumberStyles.None, CultureInfo.InvariantCulture);
            r.TemperatureKind = TemperatureKind.Value;
            r.Temperature = tsign.Value == "-" ? -magnitude : magnitude;
            if (tsign.Value == "-" && magnitude == 0) AddWarning(r, "negative zero temperature");
            return true;
        }

        static void AddWarning(Is4420Reading r, string text)
        {
            r.Warning = r.Warning == null ? text : r.Warning + "; " + text;
        }

        // The line with START_ and _END around the fields of the 29-character line (41 bytes
        // with CR LF), sent by early firmware. Recognised only to
        // explain why it is rejected.
        public static bool IsOldFormat(string content)
        {
            if (content == null || content.Length != ContentLengthNoTemperature + 10) return false;
            if (!content.StartsWith("START_", StringComparison.Ordinal) || !content.EndsWith("_END", StringComparison.Ordinal)) return false;
            string error;
            return Parse(content.Substring(6, ContentLengthNoTemperature), out error) != null;
        }

        public static string Render(IList<byte> bytes, int count)
        {
            StringBuilder sb = new StringBuilder(count + 8);
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[i];
                if (b >= 0x20 && b <= 0x7E)
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('<').Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append('>');
                }
            }
            return sb.ToString();
        }
    }

    // Splits the received byte stream into lines at LF and applies the reading rule.
    // The first fragment after the port opens is usually the end of a line already in
    // progress: if it is not a valid line it is reported as Sync, not as Rejected.
    public sealed class LineAssembler
    {
        readonly List<byte> current = new List<byte>(64);
        bool synced;

        public void Reset()
        {
            current.Clear();
            synced = false;
        }

        public void Feed(byte[] data, int offset, int count, long timestamp, DateTime wallTime, List<RxLine> output)
        {
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                if (b == 0x0A)
                {
                    output.Add(Finish(timestamp, wallTime));
                    current.Clear();
                }
                else
                {
                    current.Add(b);
                    if (current.Count >= Is4420Line.MaxLineBytes)
                    {
                        RxLine overflow = new RxLine();
                        overflow.Timestamp = timestamp;
                        overflow.WallTime = wallTime;
                        overflow.Text = Is4420Line.Render(current, current.Count);
                        overflow.Reason = "no CR LF in " + Is4420Line.MaxLineBytes.ToString(CultureInfo.InvariantCulture) + " bytes";
                        overflow.Status = synced ? LineStatus.Rejected : LineStatus.Sync;
                        synced = true;
                        output.Add(overflow);
                        current.Clear();
                    }
                }
            }
        }

        RxLine Finish(long timestamp, DateTime wallTime)
        {
            bool hasCr = current.Count > 0 && current[current.Count - 1] == 0x0D;
            int length = hasCr ? current.Count - 1 : current.Count;

            RxLine line = new RxLine();
            line.Timestamp = timestamp;
            line.WallTime = wallTime;
            line.Text = Is4420Line.Render(current, length);

            string reason = null;
            Is4420Reading reading = null;
            string text = AsciiText(length);
            if (!hasCr)
            {
                reason = "LF without CR";
            }
            else if (length != Is4420Line.ContentLength && length != Is4420Line.ContentLengthNoTemperature)
            {
                reason = length.ToString(CultureInfo.InvariantCulture) + " characters before CR LF, 37 expected";
                if (Is4420Line.IsOldFormat(text))
                {
                    reason = "old line format (START_..._END)";
                    line.OldFormat = true;
                }
            }
            else if (text == null)
            {
                reason = "byte that is not printable text";
            }
            else
            {
                string error;
                reading = Is4420Line.Parse(text, out error);
                if (reading == null) reason = error;
            }

            if (reading != null)
            {
                line.Status = LineStatus.Valid;
                line.Reading = reading;
            }
            else
            {
                line.Status = synced ? LineStatus.Rejected : LineStatus.Sync;
                line.Reason = reason;
            }
            synced = true;
            return line;
        }

        // The first 'length' bytes as text, or null if one of them is not printable ASCII.
        string AsciiText(int length)
        {
            char[] chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                byte b = current[i];
                if (b < 0x20 || b > 0x7E) return null;
                chars[i] = (char)b;
            }
            return new string(chars);
        }
    }
}
