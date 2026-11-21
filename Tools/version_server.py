"""赛马娘版本号服务：读/写 resource_version（及 server_list）。

- GET  /version          返回当前保存的 start_session JSON（游戏/工具用）
- GET  /version/value    只返回 "10021450:TZH99sxhsgYr" 这样的字符串（脚本用）
- POST /version          改版本号；需 Bearer token
- POST /version/json     整份 JSON 覆盖；需 Bearer token

数据落在同目录的 version_store.json，改完立即生效，不需要重启。
"""
import json
import os
import secrets
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE = os.path.dirname(os.path.abspath(__file__))
STORE = os.path.join(BASE, "version_store.json")
TOKEN_FILE = os.path.join(BASE, "version_token.txt")
PORT = int(os.environ.get("VERSION_PORT", "5099"))

DEFAULT_SERVER_LIST = {
    "resource_server": "prd-storage-app-umamusume.akamaized.net/dl/",
    "resource_server_cf": "prd-storage-app-umamusume.akamaized.net/dl/",
    "resource_server_ingame": "prd-storage-game-umamusume.akamaized.net/dl/",
    "resource_server_login": "prd-storage-app-umamusume.akamaized.net/dl/",
}


def load_token() -> str:
    if os.path.exists(TOKEN_FILE):
        t = open(TOKEN_FILE, encoding="utf-8").read().strip()
        if t:
            return t
    t = secrets.token_urlsafe(32)
    with open(TOKEN_FILE, "w", encoding="utf-8") as f:
        f.write(t)
    return t


def load_store() -> dict:
    if os.path.exists(STORE):
        try:
            with open(STORE, encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {
        "resource_version": "10021450:TZH99sxhsgYr",
        "server_list": dict(DEFAULT_SERVER_LIST),
    }


def save_store(data: dict) -> None:
    tmp = STORE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, STORE)


def build_response(store: dict) -> dict:
    """拼出与游戏 start_session 一致的结构（只含资源相关部分）。"""
    rv = store.get("resource_version", "")
    return {
        "response_code": 1,
        "data_headers": {
            "result_code": 1,
            "server_list": store.get("server_list", DEFAULT_SERVER_LIST),
        },
        "data": {
            "attest": False,
            "is_tutorial": False,
            "nonce": "",
            "resource_version": rv,
            "terms_updated": False,
        },
    }


class Handler(BaseHTTPRequestHandler):
    token = ""

    def log_message(self, fmt, *args):
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    def _send(self, code: int, body: bytes, ctype="application/json; charset=utf-8"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "Authorization, Content-Type")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _json(self, code: int, obj):
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

    def _authorized(self) -> bool:
        h = self.headers.get("Authorization", "")
        if not h.startswith("Bearer "):
            return False
        return secrets.compare_digest(h[7:].strip(), self.token)

    def do_OPTIONS(self):
        self._send(204, b"")

    def do_GET(self):
        path = self.path.split("?")[0].rstrip("/")
        store = load_store()
        if path in ("/version", "", "/"):
            self._json(200, build_response(store))
        elif path == "/version/value":
            rv = store.get("resource_version", "")
            self._send(200, rv.encode("utf-8"), "text/plain; charset=utf-8")
        elif path == "/version/store":
            if not self._authorized():
                self._json(200, {"ok": False, "error": "需要 Bearer token"})
                return
            self._json(200, store)
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self):
        path = self.path.split("?")[0].rstrip("/")
        if not self._authorized():
            self._json(200, {"ok": False, "error": "需要 Bearer token"})
            return

        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw or b"{}")
        except json.JSONDecodeError as e:
            self._json(200, {"ok": False, "error": f"JSON 解析失败: {e}"})
            return

        store = load_store()

        if path == "/version":
            rv = (body.get("resource_version") or "").strip()
            if not rv:
                self._json(200, {"ok": False, "error": "缺少 resource_version"})
                return
            if ":" not in rv:
                self._json(200, {"ok": False, "error": "格式应为 <版本号>:<签名>，例如 10021450:TZH99sxhsgYr"})
                return
            ver, sig = rv.split(":", 1)
            if not ver.isdigit():
                self._json(200, {"ok": False, "error": f"版本号部分必须是数字: {ver}"})
                return
            if not sig:
                self._json(200, {"ok": False, "error": "签名部分为空"})
                return
            store["resource_version"] = rv
            if isinstance(body.get("server_list"), dict):
                store["server_list"] = body["server_list"]
            save_store(store)
            self._json(200, {"ok": True, "resource_version": rv, "store": store})

        elif path == "/version/json":
            if not isinstance(body, dict):
                self._json(200, {"ok": False, "error": "需要 JSON 对象"})
                return
            save_store(body)
            self._json(200, {"ok": True, "store": body})

        else:
            self._json(404, {"error": "not found"})


def main():
    Handler.token = load_token()
    load_store()  # 首次运行落一份默认
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"version service on 127.0.0.1:{PORT}", flush=True)
    print(f"token file: {TOKEN_FILE}", flush=True)
    print(f"store file: {STORE}", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()