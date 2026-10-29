import http.server
import os
import socketserver
import sys
import time

# 本地 HTTP 服务，只服务 fixture 目录；路径里含 /slow/ 时故意 sleep 6 秒（验硬超时）。
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "root")
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8099
SLOW_SECONDS = 6.0


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def do_GET(self):
        if "/slow/" in self.path:
            print("slow: sleeping %.1fs for %s" % (SLOW_SECONDS, self.path), flush=True)
            time.sleep(SLOW_SECONDS)
        print("GET %s" % self.path, flush=True)
        super().do_GET()

    def log_message(self, fmt, *args):
        pass


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


print("serving %s on http://127.0.0.1:%d" % (os.path.abspath(ROOT), PORT), flush=True)
with Server(("127.0.0.1", PORT), Handler) as httpd:
    httpd.serve_forever()
