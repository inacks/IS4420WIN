// IS4420WIN Current Loop Receiver - COM port list and module detection.
// Detection only listens: each free port is opened 115200 8N1 for 0.6 s (DTR and RTS off),
// nothing is sent, and the port is reported as "detected" when one valid IS4420 line arrives.
// The ports are probed in parallel, so a scan takes about one second.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Inacks.IS4420Monitor
{
    public enum ProbeResult { NotProbed, Detected, OldFormat, Silent, OtherData, Busy, Failed, TimedOut, Skipped }

    public sealed class PortInfo
    {
        public string Name;          // COM7
        public bool Bluetooth;
        public ProbeResult Probe;
        public string Model;         // when detected
        public string Version;       // when detected
        public string Detail;        // error text when the port could not be opened

        public override string ToString()
        {
            return Probe == ProbeResult.Detected ? Name + "   <-- IS4420 detected!" : Name;
        }
    }

    internal static class PortScanner
    {
        public const int ListenMs = 600;
        const int OpenAllowanceMs = 1400;   // extra time for slow drivers before a port is given up

        static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        // Port names, cleaned (some drivers add junk after the number), without duplicates,
        // in natural order (COM2 before COM10).
        public static List<string> GetPortNames()
        {
            string[] raw;
            try { raw = SerialPort.GetPortNames(); }
            catch (Exception) { raw = new string[0]; }
            List<string> names = new List<string>();
            foreach (string r in raw)
            {
                if (String.IsNullOrEmpty(r)) continue;
                string n = r;
                if (IsWindows)
                {
                    Match m = Regex.Match(r, "^COM[0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!m.Success) continue;
                    n = m.Value.ToUpperInvariant();
                }
                if (!Contains(names, n)) names.Add(n);
            }
            names.Sort(NaturalCompare);
            return names;
        }

        public static bool Contains(List<string> names, string name)
        {
            foreach (string n in names)
            {
                if (String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static int NaturalCompare(string a, string b)
        {
            string pa, pb;
            long na, nb;
            Split(a, out pa, out na);
            Split(b, out pb, out nb);
            int c = String.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            if (na != nb) return na < nb ? -1 : 1;
            return String.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static void Split(string s, out string prefix, out long number)
        {
            int i = s.Length;
            while (i > 0 && s[i - 1] >= '0' && s[i - 1] <= '9') i--;
            prefix = s.Substring(0, i);
            number = -1;
            if (i < s.Length && s.Length - i <= 9)
            {
                number = long.Parse(s.Substring(i), CultureInfo.InvariantCulture);
            }
        }

        // COM name -> kernel device name (for example \Device\BthModem0), from the registry key
        // the serial drivers fill in. Used to leave Bluetooth ports out of the scan: opening
        // them makes Windows try to connect to the paired device, which can take many seconds.
        static Dictionary<string, string> GetDeviceNames()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!IsWindows) return d;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey("HARDWARE\\DEVICEMAP\\SERIALCOMM"))
                {
                    if (k == null) return d;
                    foreach (string valueName in k.GetValueNames())
                    {
                        string com = k.GetValue(valueName) as string;
                        if (com == null) continue;
                        Match m = Regex.Match(com, "^COM[0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (m.Success) d[m.Value.ToUpperInvariant()] = valueName;
                    }
                }
            }
            catch (Exception)
            {
            }
            return d;
        }

        sealed class ProbeJob
        {
            public string Name;
            public Thread Thread;
            public volatile bool Abandoned;
            readonly object sync = new object();
            bool done;
            ProbeResult result;
            string model, version, detail;

            public void Complete(ProbeResult r, string m, string v, string d)
            {
                lock (sync)
                {
                    done = true;
                    result = r;
                    model = m;
                    version = v;
                    detail = d;
                }
            }

            public bool CopyTo(PortInfo p)
            {
                lock (sync)
                {
                    if (!done) return false;
                    p.Probe = result;
                    p.Model = model;
                    p.Version = version;
                    p.Detail = detail;
                    return true;
                }
            }
        }

        // Lists the ports and listens on each one that is free. Ports in 'skip' (the one this
        // program has open) are listed but not probed.
        public static List<PortInfo> Scan(ICollection<string> skip)
        {
            Stopwatch clock = Stopwatch.StartNew();
            List<string> names = GetPortNames();
            Dictionary<string, string> devices = GetDeviceNames();

            List<PortInfo> ports = new List<PortInfo>();
            List<KeyValuePair<PortInfo, ProbeJob>> jobs = new List<KeyValuePair<PortInfo, ProbeJob>>();
            foreach (string n in names)
            {
                PortInfo p = new PortInfo();
                p.Name = n;
                string device;
                if (devices.TryGetValue(n, out device) && device.IndexOf("BthModem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    p.Bluetooth = true;
                }
                bool skipped = false;
                if (skip != null)
                {
                    foreach (string s in skip)
                    {
                        if (String.Equals(s, n, StringComparison.OrdinalIgnoreCase)) skipped = true;
                    }
                }
                if (skipped || p.Bluetooth)
                {
                    p.Probe = ProbeResult.Skipped;
                }
                else
                {
                    ProbeJob job = new ProbeJob();
                    job.Name = n;
                    ProbeJob captured = job;
                    job.Thread = new Thread(delegate() { ProbeOne(captured); });
                    job.Thread.IsBackground = true;
                    job.Thread.Name = "Probe " + n;
                    job.Thread.Start();
                    jobs.Add(new KeyValuePair<PortInfo, ProbeJob>(p, job));
                }
                ports.Add(p);
            }

            int deadline = ListenMs + OpenAllowanceMs;
            foreach (KeyValuePair<PortInfo, ProbeJob> j in jobs)
            {
                int remaining = deadline - (int)clock.ElapsedMilliseconds;
                if (remaining > 0) j.Value.Thread.Join(remaining);
                if (!j.Value.CopyTo(j.Key))
                {
                    // Still opening. Leave it: the thread closes the port as soon as Open returns.
                    j.Value.Abandoned = true;
                    j.Key.Probe = ProbeResult.TimedOut;
                }
            }

            return ports;
        }

        static void ProbeOne(ProbeJob job)
        {
            SerialPort sp = null;
            ProbeResult result = ProbeResult.Silent;
            string model = null, version = null, detail = null;
            try
            {
                sp = PortFactory.Create(job.Name, 50);
                sp.Open();
                if (job.Abandoned)
                {
                    result = ProbeResult.TimedOut;
                }
                else
                {
                    PortFactory.AfterOpen(sp);
                    LineAssembler assembler = new LineAssembler();
                    List<RxLine> lines = new List<RxLine>();
                    byte[] buffer = new byte[1024];
                    long total = 0;
                    bool oldFormat = false;
                    Stopwatch sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < ListenMs && !job.Abandoned)
                    {
                        int n;
                        try
                        {
                            n = sp.Read(buffer, 0, buffer.Length);
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                        if (n <= 0)
                        {
                            Thread.Sleep(10);
                            continue;
                        }
                        total += n;
                        assembler.Feed(buffer, 0, n, 0, DateTime.Now, lines);
                        foreach (RxLine line in lines)
                        {
                            if (line.OldFormat) oldFormat = true;
                            if (line.Status == LineStatus.Valid)
                            {
                                result = ProbeResult.Detected;
                                model = line.Reading.Model;
                                version = line.Reading.Version;
                                break;
                            }
                        }
                        lines.Clear();
                        if (result == ProbeResult.Detected) break;
                    }
                    if (result != ProbeResult.Detected)
                    {
                        result = oldFormat ? ProbeResult.OldFormat : total > 0 ? ProbeResult.OtherData : ProbeResult.Silent;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                result = ProbeResult.Busy;
            }
            catch (Exception ex)
            {
                result = ProbeResult.Failed;
                detail = ex.Message;
            }
            finally
            {
                PortFactory.SafeClose(sp);
                job.Complete(result, model, version, detail);
            }
        }
    }
}
