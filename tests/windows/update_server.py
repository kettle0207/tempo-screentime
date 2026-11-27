"""Loopback stand-in for GitHub Releases, used to drive the real installed app's updater.

    python update_server.py <feed-dir> <port> <mode>
feed-dir contains releases/latest/download/tempo-update.txt and releases/download/v<ver>/<file>.
mode: normal | truncate (cut the installer halfway) | badsig (serve badsig/tempo-update.txt)
      | corrupt (flip one byte of the installer, same length)
Every request line is appended to <feed-dir>/requests.log so the test can check what was sent.
"""
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

feed, port, mode = Path(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
log = feed / "requests.log"


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        with log.open("a", encoding="utf-8") as f:
            headers = {k: v for k, v in self.headers.items() if k.lower() not in ("host", "connection")}
            f.write(f"{mode} {self.path} {headers}\n")
        path = self.path.split("?")[0]
        if path.endswith("/latest/download/tempo-update.txt"):
            file = feed / ("badsig/tempo-update.txt" if mode == "badsig" else "releases/latest/download/tempo-update.txt")
        elif "/releases/download/v" in path:
            file = feed / path.lstrip("/")
        else:
            file = None
        if not file or not file.is_file():
            self.send_error(404)
            return
        data = bytearray(file.read_bytes())
        is_installer = file.suffix == ".exe"
        if is_installer and mode == "corrupt":
            data[len(data) // 2] ^= 0xFF
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        if is_installer and mode == "truncate":
            self.wfile.write(data[: len(data) // 2])
            self.wfile.flush()
            self.connection.close()
            return
        self.wfile.write(data)

    def log_message(self, *args):
        pass


ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
