using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Receives gaze packets from the Mac tracker (3DTracker/Orlosky3DEyeTracker.py) on a background thread.
/// Packet, one ASCII line per UDP datagram:
///   seq, mac_time, origin_x, origin_y, origin_z, dir_x, dir_y, dir_z, confidence
/// confidence 0 = no pupil in that frame (blink, eye out of view).
/// </summary>
public class GazeUdpReceiver : MonoBehaviour
{
    public struct Sample
    {
        public long seq;
        public double macTime;      // tracker clock (Mac), seconds
        public double receiveTime;  // local clock, seconds since this component started
        public Vector3 origin;
        public Vector3 direction;
        public float confidence;
    }

    [Tooltip("Must match --udp-port on the Mac (default 5005).")]
    public int port = 5005;
    [Tooltip("Print packet rate and loss to the console every few seconds.")]
    public bool logStats = true;
    public float statsInterval = 5f;

    private readonly object gate = new object();
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private Sample latest;
    private bool hasSample;
    private long received, lost, badPackets, lastSeq = -1;
    private long receivedAtLastStats;

    private UdpClient client;
    private Thread thread;
    private volatile bool running;
    private float nextStatsTime;

    public long Received { get { lock (gate) return received; } }
    public long Lost { get { lock (gate) return lost; } }
    public double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Seconds since the last packet arrived, or infinity if none yet.</summary>
    public double SecondsSinceLastPacket
    {
        get { lock (gate) return hasSample ? Now - latest.receiveTime : double.PositiveInfinity; }
    }

    public bool TryGetLatest(out Sample sample)
    {
        lock (gate)
        {
            sample = latest;
            return hasSample;
        }
    }

    private void OnEnable()
    {
        try
        {
            client = new UdpClient(port);
            client.Client.ReceiveTimeout = 500;
        }
        catch (Exception e)
        {
            Debug.LogError($"[GazeUdp] Cannot open UDP port {port}: {e.Message}");
            return;
        }
        running = true;
        thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "GazeUdpReceiver" };
        thread.Start();
        nextStatsTime = Time.realtimeSinceStartup + statsInterval;
        Debug.Log($"[GazeUdp] Listening on UDP port {port}");
    }

    private void OnDisable()
    {
        running = false;
        try { client?.Close(); } catch (Exception) { }
        if (thread != null && thread.IsAlive) thread.Join(1000);
        thread = null;
        client = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            byte[] data;
            try
            {
                data = client.Receive(ref from);
            }
            catch (SocketException) { continue; }          // timeout, or socket closed while stopping
            catch (ObjectDisposedException) { break; }

            if (!TryParse(data, out Sample s))
            {
                lock (gate) badPackets++;
                continue;
            }
            s.receiveTime = Now;
            lock (gate)
            {
                if (lastSeq >= 0 && s.seq > lastSeq + 1) lost += s.seq - lastSeq - 1;
                lastSeq = s.seq;                           // a lower seq means the tracker restarted
                latest = s;
                hasSample = true;
                received++;
            }
        }
    }

    private static bool TryParse(byte[] data, out Sample s)
    {
        s = default;
        string[] f = Encoding.ASCII.GetString(data).Trim().Split(',');
        if (f.Length != 9) return false;
        var inv = CultureInfo.InvariantCulture;
        if (!long.TryParse(f[0], NumberStyles.Integer, inv, out s.seq)) return false;
        if (!double.TryParse(f[1], NumberStyles.Float, inv, out s.macTime)) return false;
        float[] v = new float[7];
        for (int i = 0; i < 7; i++)
            if (!float.TryParse(f[i + 2], NumberStyles.Float, inv, out v[i])) return false;
        s.origin = new Vector3(v[0], v[1], v[2]);
        s.direction = new Vector3(v[3], v[4], v[5]);
        s.confidence = v[6];
        return true;
    }

    private void Update()
    {
        if (!logStats || Time.realtimeSinceStartup < nextStatsTime) return;
        long r, l, b;
        lock (gate) { r = received; l = lost; b = badPackets; }
        float rate = (r - receivedAtLastStats) / statsInterval;
        receivedAtLastStats = r;
        nextStatsTime = Time.realtimeSinceStartup + statsInterval;
        Debug.Log($"[GazeUdp] {rate:F0} packets/s, total {r}, lost {l}, bad {b}");
    }
}
