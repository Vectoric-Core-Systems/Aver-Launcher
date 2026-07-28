"""
A static file server that honours HTTP Range, for testing the download path.

    python tools/range-server.py <dir> [port]

Python's own http.server does NOT support Range: it answers every request with the whole file and a
200. That is not a detail. Against it the installer measured 300% of the pack transferred over three
requests -- worse than no deduplication at all -- because each range pulled the entire object. The
CDN behind GitHub release assets does support Range, so testing against a server that does not
measures the wrong thing and hides the bug.
"""

import os
import re
import sys
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

RANGE_RE = re.compile(r"bytes=(\d+)-(\d*)")


class RangeHandler(SimpleHTTPRequestHandler):
    def send_head(self):
        rng = self.headers.get("Range")
        if not rng:
            return super().send_head()

        m = RANGE_RE.fullmatch(rng.strip())
        if not m:
            return super().send_head()

        path = self.translate_path(self.path)
        if not os.path.isfile(path):
            self.send_error(404)
            return None

        size = os.path.getsize(path)
        start = int(m.group(1))
        end = int(m.group(2)) if m.group(2) else size - 1
        end = min(end, size - 1)

        if start > end or start >= size:
            self.send_response(416)
            self.send_header("Content-Range", f"bytes */{size}")
            self.end_headers()
            return None

        f = open(path, "rb")
        f.seek(start)
        length = end - start + 1

        self.send_response(206)
        self.send_header("Content-Type", self.guess_type(path))
        self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        self.end_headers()

        # SimpleHTTPRequestHandler copies to EOF, so hand it a reader bounded to the range.
        return _Bounded(f, length)

    def log_message(self, fmt, *a):  # quiet; the gate prints what matters
        pass


class _Bounded:
    """A file-like object that stops after n bytes, so copyfile cannot overrun the range."""

    def __init__(self, f, n):
        self._f = f
        self._left = n

    def read(self, size=-1):
        if self._left <= 0:
            return b""
        if size is None or size < 0 or size > self._left:
            size = self._left
        data = self._f.read(size)
        self._left -= len(data)
        return data

    def close(self):
        self._f.close()


def main() -> int:
    directory = sys.argv[1] if len(sys.argv) > 1 else "."
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 8899
    handler = partial(RangeHandler, directory=directory)
    srv = ThreadingHTTPServer(("127.0.0.1", port), handler)
    print(f"serving {directory} on http://127.0.0.1:{port} (Range supported)", flush=True)
    srv.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
