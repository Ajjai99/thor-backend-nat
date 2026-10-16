# Probes egress from inside the VPC. Each check resolves a name through the VPC resolver (where the DNS
# Firewall sits) and, if it resolves, opens a TLS connection out through the NAT.
import random
import socket
import ssl
import struct

TIMEOUT = 4

DEFAULT_CHECKS = [
    # (name, what the rules should do with it)
    ("pypi.org", "BLOCK  rule 100 package registry"),
    ("deb.debian.org", "BLOCK  rule 100 package registry"),
    ("sts.us-east-1.amazonaws.com", "ALLOW  rule 200 *.amazonaws.com"),
    ("example.org", "ALLOW  rule 200 egress_allowed_domains (exact name)"),
    ("www.example.org", "DEFAULT rule 300 exact entry does not cover subdomains"),
    ("www.wikipedia.org", "DEFAULT rule 300 until added to tenant-vendors list (rule 210)"),
    ("example.com", "DEFAULT rule 300"),
]


def resolve_and_connect(name):
    try:
        ip = socket.getaddrinfo(name, 443, socket.AF_INET, socket.SOCK_STREAM)[0][4][0]
    except socket.gaierror as e:
        return {"dns": f"FAILED ({e.strerror})", "https": "-"}
    try:
        ctx = ssl.create_default_context()
        with socket.create_connection((ip, 443), timeout=TIMEOUT) as s:
            with ctx.wrap_socket(s, server_hostname=name):
                pass
        https = "CONNECTED"
    except Exception as e:  # noqa: BLE001 — any failure is just a result to report
        https = f"FAILED ({type(e).__name__})"
    return {"dns": ip, "https": https}


def raw_ip_tls(ip, sni=None):
    """No DNS lookup happens, so the DNS Firewall never sees this. A full TLS handshake (not just a TCP connect) is
    needed to see Network Firewall's verdict: it lets the TCP handshake through and judges the TLS ClientHello's SNI.
    With sni set, the ClientHello claims an allowed name while going to an arbitrary IP (SNI spoofing)."""
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    try:
        with socket.create_connection((ip, 443), timeout=TIMEOUT) as s:
            with ctx.wrap_socket(s, server_hostname=sni):
                return "TLS HANDSHAKE OK"
    except Exception as e:  # noqa: BLE001
        return f"FAILED ({type(e).__name__})"


def external_resolver_query(name, resolver):
    """Known gap: asks a public resolver directly over UDP/53, bypassing the VPC resolver."""
    qid = random.randint(0, 0xFFFF)
    qname = b"".join(bytes([len(p)]) + p.encode() for p in name.split(".")) + b"\0"
    packet = struct.pack(">HHHHHH", qid, 0x0100, 1, 0, 0, 0) + qname + struct.pack(">HH", 1, 1)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.settimeout(TIMEOUT)
            s.sendto(packet, (resolver, 53))
            data, _ = s.recvfrom(512)
    except Exception as e:  # noqa: BLE001
        return f"FAILED ({type(e).__name__})"
    rcode = data[3] & 0x0F
    answers = struct.unpack(">H", data[6:8])[0]
    return f"ANSWERED rcode={rcode} answers={answers}"


def handler(event, _context):
    names = event.get("names") or [n for n, _ in DEFAULT_CHECKS]
    expected = dict(DEFAULT_CHECKS)
    results = [{"name": n, "expect": expected.get(n, ""), **resolve_and_connect(n)} for n in names]
    return {
        "checks": results,
        "bypass": {
            "raw_ip_1.1.1.1:443 (no SNI)": raw_ip_tls("1.1.1.1"),
            "raw_ip_1.1.1.1:443 (SNI example.org)": raw_ip_tls("1.1.1.1", "example.org"),
            "dns_via_8.8.8.8_for_example.com": external_resolver_query("example.com", "8.8.8.8"),
        },
    }
