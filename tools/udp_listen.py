"""Print gaze packets from the tracker, to test the UDP link on the Mac.

Packet (one ASCII line per frame, comma separated):
    seq, mac_time, origin_x, origin_y, origin_z, dir_x, dir_y, dir_z, confidence
confidence 0 means no pupil in that frame.

Run in a second Terminal, then start the tracker:
    python tools/udp_listen.py              # listen on port 5005
    python tools/udp_listen.py --every 1    # print every packet
    python tools/udp_listen.py --csv log.csv

Once a second it prints: packet rate, share of frames with a pupil, lost packets
(gaps in seq) and delay (only meaningful when tracker and listener run on the same Mac).
"""
import argparse
import socket
import statistics
import time


def parse(data):
    f = data.decode("ascii").strip().split(",")
    if len(f) != 9:
        raise ValueError(f"expected 9 fields, got {len(f)}")
    seq = int(f[0])
    t, ox, oy, oz, dx, dy, dz, conf = map(float, f[1:])
    return seq, t, (ox, oy, oz), (dx, dy, dz), conf


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="0.0.0.0", help="interface to listen on (default: all)")
    ap.add_argument("--port", type=int, default=5005)
    ap.add_argument("--every", type=int, default=15, help="print every Nth packet (default 15, about twice a second)")
    ap.add_argument("--csv", help="also append all packets to this CSV file")
    ap.add_argument("--seconds", type=float, help="stop after this many seconds")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((args.host, args.port))
    sock.settimeout(0.5)
    out = open(args.csv, "a") if args.csv else None
    if out and out.tell() == 0:
        out.write("seq,mac_time,origin_x,origin_y,origin_z,dir_x,dir_y,dir_z,confidence,recv_time\n")
    print(f"Listening on {args.host}:{args.port} ... (Ctrl+C to stop)")

    start = time.time()
    total = valid_total = lost_total = 0
    win_n = win_valid = 0
    win_delay = []
    last_seq = None
    silent = False
    next_report = time.time() + 1.0
    try:
        while args.seconds is None or time.time() - start < args.seconds:
            try:
                data, addr = sock.recvfrom(1024)
            except socket.timeout:
                data = None
            now = time.time()
            if data:
                try:
                    seq, t, o, d, conf = parse(data)
                except ValueError as e:
                    print(f"bad packet from {addr[0]}: {e}: {data[:80]!r}")
                    continue
                if last_seq is not None and seq > last_seq + 1:
                    lost_total += seq - last_seq - 1
                if last_seq is not None and seq < last_seq:
                    print(f"seq went back {last_seq} -> {seq} (tracker restarted?)")
                last_seq = seq
                total += 1; win_n += 1
                if conf > 0:
                    valid_total += 1; win_valid += 1
                win_delay.append((now - t) * 1000)
                if out:
                    out.write(data.decode("ascii").strip() + f",{now:.6f}\n")
                if args.every and seq % args.every == 0:
                    print(f"#{seq:6d}  dir ({d[0]:+.3f}, {d[1]:+.3f}, {d[2]:+.3f})  origin ({o[0]:+.2f}, {o[1]:+.2f}, {o[2]:+.2f})  conf {conf:.2f}")
            if now >= next_report:
                if win_n:
                    silent = False
                    print(f"  -- {win_n} packets/s, pupil in {100*win_valid/win_n:.0f}%, lost so far {lost_total}, "
                          f"delay median {statistics.median(win_delay):.1f} ms")
                elif not silent:
                    print("  -- no packets (waiting for the tracker)")
                    silent = True
                win_n = win_valid = 0; win_delay = []
                next_report = now + 1.0
    except KeyboardInterrupt:
        pass
    finally:
        if out:
            out.close()
    if total:
        print(f"\n{total} packets, pupil in {100*valid_total/total:.0f}%, lost {lost_total} "
              f"({100*lost_total/(total+lost_total):.1f}%)")


if __name__ == "__main__":
    main()
