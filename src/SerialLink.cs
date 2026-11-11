// IS4420WIN Current Loop Receiver - serial port reader.
// The port is opened 115200 8N1 with DTR and RTS off, and nothing is ever written to it.
// A background thread owns the port: it opens it, reads it and closes it. Results go to
// the UI thread through a queue, tagged with a session number so that events from an
// older connection are ignored.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace Inacks.IS4420Monitor
{
    public enum LinkEventKind { Opened, OpenFailed, Data, Lost, Closed }

    public sealed class LinkEvent
    {
        public int Session;
        public LinkEventKind Kind;
        public string Message;
        public List<RxLine> Lines;
    }

    internal static class PortFactory
    {
        public const int BaudRate = 115200;

        public static SerialPort Create(string name, int readTimeoutMs)
        {
            SerialPort sp = new SerialPort(name, BaudRate, Parity.None, 8, StopBits.One);
            sp.Handshake = Handshake.None;
            sp.DtrEnable = false;
            sp.RtsEnable = false;
            sp.ReadTimeout = readTimeoutMs;
            sp.ReadBufferSize = 16384;
            return sp;
        }

        public static void AfterOpen(SerialPort sp)
        {
            // .NET Framework: if a USB adapter is unplugged while open, the stream finalizer can
            // throw on the finalizer thread and end the process. The port is always closed
            // explicitly (SafeClose), so the finalizer is not needed.
            try { GC.SuppressFinalize(sp.BaseStream); } catch (Exception) { }
            // Drop whatever the driver kept from before the port was opened.
            try { sp.DiscardInBuffer(); } catch (Exception) { }
        }

        public static void SafeClose(SerialPort sp)
        {
            if (sp == null) return;
            try { if (sp.IsOpen) sp.Close(); } catch (Exception) { }
            try { sp.Dispose(); } catch (Exception) { }
        }

        public static string Describe(Exception ex, string portName)
        {
            if (ex is UnauthorizedAccessException)
                return portName + " is in use by another program";
            if (ex is IOException || ex is InvalidOperationException || ex is ArgumentException)
                return ex.Message;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    internal sealed class SerialLink
    {
        sealed class ReaderState
        {
            public volatile bool Stop;
        }

        readonly ConcurrentQueue<LinkEvent> events;
        Thread thread;
        ReaderState current;
        int session;

        public SerialLink(ConcurrentQueue<LinkEvent> events)
        {
            this.events = events;
        }

        public bool IsRunning
        {
            get { Thread t = thread; return t != null && t.IsAlive; }
        }

        // Starts a new connection and returns its session number.
        public int Start(string portName)
        {
            Stop();
            session++;
            int s = session;
            ReaderState st = new ReaderState();
            Thread t = new Thread(delegate() { Run(portName, s, st); });
            t.IsBackground = true;
            t.Name = "Serial reader " + portName;
            current = st;
            thread = t;
            t.Start();
            return s;
        }

        // Asks the reader to stop and waits for it to close the port (up to 1.5 s).
        public void Stop()
        {
            ReaderState st = current;
            Thread t = thread;
            current = null;
            thread = null;
            if (st != null) st.Stop = true;
            if (t != null && t.IsAlive)
            {
                t.Join(1500);
            }
        }

        void Post(int s, LinkEventKind kind, string message, List<RxLine> lines)
        {
            LinkEvent ev = new LinkEvent();
            ev.Session = s;
            ev.Kind = kind;
            ev.Message = message;
            ev.Lines = lines;
            events.Enqueue(ev);
        }

        // Windows may need a moment to release a port that was just closed (the scan has
        // just listened on it), and Open fails with "access denied" meanwhile. So a failed
        // Open is retried for up to OpenRetryMs before it is reported.
        const int OpenRetryMs = 2000;

        static void OpenWithRetry(SerialPort sp, ReaderState st)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    sp.Open();
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    if (st.Stop || sw.ElapsedMilliseconds >= OpenRetryMs) throw;
                }
                catch (IOException)
                {
                    if (st.Stop || sw.ElapsedMilliseconds >= OpenRetryMs) throw;
                }
                Thread.Sleep(150);
            }
        }

        void Run(string portName, int s, ReaderState st)
        {
            SerialPort sp = null;
            bool opened = false;
            try
            {
                sp = PortFactory.Create(portName, 100);
                OpenWithRetry(sp, st);
                opened = true;
                PortFactory.AfterOpen(sp);
                Post(s, LinkEventKind.Opened, null, null);

                LineAssembler assembler = new LineAssembler();
                byte[] buffer = new byte[4096];
                List<RxLine> lines = new List<RxLine>();
                while (!st.Stop)
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
                        Thread.Sleep(20);
                        continue;
                    }
                    assembler.Feed(buffer, 0, n, Stopwatch.GetTimestamp(), DateTime.Now, lines);
                    if (lines.Count > 0)
                    {
                        Post(s, LinkEventKind.Data, null, lines);
                        lines = new List<RxLine>();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!st.Stop)
                {
                    Post(s, opened ? LinkEventKind.Lost : LinkEventKind.OpenFailed,
                        PortFactory.Describe(ex, portName), null);
                }
            }
            finally
            {
                PortFactory.SafeClose(sp);
                Post(s, LinkEventKind.Closed, null, null);
            }
        }
    }
}
