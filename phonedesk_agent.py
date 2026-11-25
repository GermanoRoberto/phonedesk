"""
DeskDock - Agent do Computador (Windows / macOS / Linux)
Servidor HTTP + Telemetria de Hardware + Disparo de Ações + Descoberta UDP
100% Autocontido: usa apenas a biblioteca padrão do Python 3
"""

import sys
import os
import json
import time
import socket
import threading
import subprocess
import shutil
import urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
import ctypes

PORT = 8770
UDP_PORT = 8771
VERSION = "1.0.0"

# Caminho do diretório atual (onde estão index.html, static, etc.)
BASE_DIR = os.path.dirname(os.path.abspath(__file__))

# Obter IPs locais
def get_local_ips():
    ips = []
    try:
        hostname = socket.gethostname()
        for info in socket.getaddrinfo(hostname, None, socket.AF_INET):
            ip = info[4][0]
            if not ip.startswith("127.") and ip not in ips:
                ips.append(ip)
    except Exception:
        pass
    if not ips:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            ips.append(s.getsockname()[0])
            s.close()
        except Exception:
            ips.append("127.0.0.1")
    return ips

# Obter telemetria do Windows sem bibliotecas externas
def get_windows_memory():
    try:
        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [
                ("dwLength", ctypes.c_ulong),
                ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("sullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]
        stat = MEMORYSTATUSEX()
        stat.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat))
        total_gb = round(stat.ullTotalPhys / (1024**3), 1)
        used_gb = round((stat.ullTotalPhys - stat.ullAvailPhys) / (1024**3), 1)
        return {
            "load_percent": stat.dwMemoryLoad,
            "total_gb": total_gb,
            "used_gb": used_gb
        }
    except Exception:
        return {"load_percent": 0, "total_gb": 0, "used_gb": 0}

# Amostragem de CPU sem psutil
_last_cpu_time = 0
_last_idle_time = 0

def get_windows_cpu():
    global _last_cpu_time, _last_idle_time
    try:
        class FILETIME(ctypes.Structure):
            _fields_ = [("dwLowDateTime", ctypes.c_ulong), ("dwHighDateTime", ctypes.c_ulong)]
        
        idleTime = FILETIME()
        kernelTime = FILETIME()
        userTime = FILETIME()
        
        if ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idleTime), ctypes.byref(kernelTime), ctypes.byref(userTime)):
            idle = (idleTime.dwHighDateTime << 32) + idleTime.dwLowDateTime
            kernel = (kernelTime.dwHighDateTime << 32) + kernelTime.dwLowDateTime
            user = (userTime.dwHighDateTime << 32) + userTime.dwLowDateTime
            total = kernel + user
            
            if _last_cpu_time > 0 and total > _last_cpu_time:
                d_total = total - _last_cpu_time
                d_idle = idle - _last_idle_time
                cpu_load = max(0, min(100, int((1.0 - (d_idle / d_total)) * 100)))
            else:
                cpu_load = 5
            
            _last_cpu_time = total
            _last_idle_time = idle
            return cpu_load
    except Exception:
        pass
    return 10

def get_windows_disk():
    try:
        du = shutil.disk_usage("C:\\")
        total_gb = round(du.total / (1024**3), 1)
        free_gb = round(du.free / (1024**3), 1)
        used_gb = round(du.used / (1024**3), 1)
        used_pct = round((du.used / du.total) * 100, 1)
        return {
            "total_gb": total_gb,
            "free_gb": free_gb,
            "used_gb": used_gb,
            "used_percent": used_pct
        }
    except Exception:
        return {"total_gb": 512, "free_gb": 256, "used_gb": 256, "used_percent": 50}

def get_windows_uptime():
    try:
        uptime_min = int(ctypes.windll.kernel32.GetTickCount64() / 60000)
        h = uptime_min // 60
        m = uptime_min % 60
        return f"{h}h {m}m"
    except Exception:
        return "1h 0m"

# Ações de Teclado / Multimídia no Windows
VK_VOLUME_MUTE = 0xAD
VK_VOLUME_DOWN = 0xAE
VK_VOLUME_UP = 0xAF
VK_MEDIA_NEXT_TRACK = 0xB0
VK_MEDIA_PREV_TRACK = 0xB1
VK_MEDIA_PLAY_PAUSE = 0xB3

def send_key(vk_code):
    try:
        ctypes.windll.user32.keybd_event(vk_code, 0, 0, 0)
        time.sleep(0.05)
        ctypes.windll.user32.keybd_event(vk_code, 0, 2, 0) # KEYEVENTF_KEYUP
        return True
    except Exception as e:
        print(f"[Error send_key] {e}")
        return False

# Mapeamento de Teclas de Atalho do Windows (compatibilidade total com PhoneBox / Stream Deck)
VK_MAP = {
    'ctrl': 0x11, 'control': 0x11,
    'alt': 0x12, 'menu': 0x12,
    'shift': 0x10,
    'win': 0x5B, 'windows': 0x5B,
    'enter': 0x0D, 'return': 0x0D,
    'esc': 0x1B, 'escape': 0x1B,
    'space': 0x20,
    'tab': 0x09,
    'backspace': 0x08,
    'delete': 0x2E,
    'up': 0x26, 'down': 0x28, 'left': 0x25, 'right': 0x27,
    'f1': 0x70, 'f2': 0x71, 'f3': 0x72, 'f4': 0x73, 'f5': 0x74, 'f6': 0x75,
    'f7': 0x76, 'f8': 0x77, 'f9': 0x78, 'f10': 0x79, 'f11': 0x7A, 'f12': 0x7B
}

def send_combo(combo_str):
    parts = [p.strip().lower() for p in combo_str.split('+') if p.strip()]
    vks = []
    for p in parts:
        if p in VK_MAP:
            vks.append(VK_MAP[p])
        elif len(p) == 1:
            vks.append(ord(p.upper()))
        else:
            print(f"[Hotkey] Tecla não mapeada: {p}")
            return False
    # Pressiona em ordem
    for vk in vks:
        ctypes.windll.user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    # Solta na ordem inversa
    for vk in reversed(vks):
        ctypes.windll.user32.keybd_event(vk, 0, 2, 0)
    return True

def send_hotkey(spec):
    try:
        combos = [c.strip() for c in spec.split(',') if c.strip()]
        for c in combos:
            send_combo(c)
            time.sleep(0.05)
        print(f"[Hotkey Success] Disparou atalho: {spec}")
        return True
    except Exception as e:
        print(f"[Hotkey Error] Falha no atalho {spec}: {e}")
        return False

def lock_workstation():
    try:
        ctypes.windll.user32.LockWorkStation()
        return True
    except Exception as e:
        print(f"[Error lock_workstation] {e}")
        return False

def launch_app(target):
    if not target:
        return False
    app_key = target.lower().strip().replace(".exe", "")
    print(f"[Launcher] Request to open: {target} (normalized: {app_key})")
    
    CHROME_EXE = os.path.expandvars(r"%ProgramFiles%\Google\Chrome\Application\chrome.exe")
    ANTIGRAVITY = os.path.expandvars(r"%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe")
    TEAMVIEWER = os.path.expandvars(r"%ProgramFiles%\TeamViewer\TeamViewer.exe")
    TEAMVIEWER_X86 = os.path.expandvars(r"%ProgramFiles(x86)%\TeamViewer\TeamViewer.exe")
    OUTLOOK = os.path.expandvars(r"%ProgramFiles%\Microsoft Office\root\Office16\OUTLOOK.EXE")
    SPOTIFY = os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WindowsApps\Spotify.exe")

    try:
        if app_key == "antigravity":
            if os.path.exists(ANTIGRAVITY):
                os.startfile(ANTIGRAVITY)
                return True
            subprocess.Popen(["Antigravity.exe"], shell=True)
            return True

        elif app_key == "teamviewer":
            if os.path.exists(TEAMVIEWER):
                os.startfile(TEAMVIEWER)
                return True
            if os.path.exists(TEAMVIEWER_X86):
                os.startfile(TEAMVIEWER_X86)
                return True
            subprocess.Popen(["TeamViewer.exe"], shell=True)
            return True

        elif app_key == "sandbox":
            subprocess.Popen(["powershell.exe", "-Command", "Start-Process WindowsSandbox.exe"])
            return True

        elif app_key == "outlook":
            if os.path.exists(OUTLOOK):
                os.startfile(OUTLOOK)
                return True
            os.startfile("outlook.exe")
            return True

        elif app_key in ["whatsapp", "wa"]:
            os.startfile("whatsapp:")
            return True

        elif app_key == "spotify":
            try:
                os.startfile("spotify:")
                return True
            except Exception:
                if os.path.exists(SPOTIFY):
                    os.startfile(SPOTIFY)
                    return True
                subprocess.Popen(["spotify.exe"], shell=True)
                return True

        elif app_key in ["explorer", "arquivos"]:
            os.startfile("explorer.exe")
            return True

        elif app_key in ["chrome", "navegador"]:
            if os.path.exists(CHROME_EXE):
                os.startfile(CHROME_EXE)
                return True
            subprocess.Popen(["chrome.exe"], shell=True)
            return True

        elif app_key in ["terminal", "cmd", "powershell"]:
            subprocess.Popen(["powershell.exe"])
            return True

        # Fallbacks genéricos
        if os.path.exists(target):
            os.startfile(target)
            return True
        os.startfile(target)
        return True
    except Exception as e:
        print(f"[Launcher Error] Falha ao abrir {target}: {e}")
        try:
            subprocess.Popen(target, shell=True)
            return True
        except Exception as e2:
            print(f"[Launcher Shell Error] {e2}")
            return False

def execute_action(action_type, payload):
    """Executa ações no computador com segurança e velocidade."""
    print(f"[Action] {action_type} -> {payload}")
    if action_type == "lock":
        return lock_workstation()
    
    elif action_type == "media":
        cmd = payload.get("command", "")
        if cmd == "mute":
            return send_key(VK_VOLUME_MUTE)
        elif cmd == "volup":
            return send_key(VK_VOLUME_UP)
        elif cmd == "voldown":
            return send_key(VK_VOLUME_DOWN)
        elif cmd == "playpause":
            return send_key(VK_MEDIA_PLAY_PAUSE)
        elif cmd == "next":
            return send_key(VK_MEDIA_NEXT_TRACK)
        elif cmd == "prev":
            return send_key(VK_MEDIA_PREV_TRACK)
            
    elif action_type in ["hotkey", "shortcut", "key", "keys"]:
        spec = payload.get("keys") or payload.get("command") or payload.get("target") or ""
        return send_hotkey(spec)

    elif action_type == "run_shortcut":
        sid = payload.get("id") or payload.get("target") or ""
        if not sid:
            return False
        try:
            import urllib.request
            req = urllib.request.Request(
                f"http://{PHONEBOX_HOST}:{PHONEBOX_PORT}/api/run/{urllib.parse.quote(str(sid))}",
                data=b"",
                method="POST",
            )
            with urllib.request.urlopen(req, timeout=5) as resp:
                return 200 <= resp.status < 300
        except Exception as e:
            print(f"[Shortcut Proxy Error] {e}")
            return False

    elif action_type == "launch":
        target = payload.get("target", "")
        if not target:
            return False
        if target.startswith("http://") or target.startswith("https://"):
            import webbrowser
            webbrowser.open(target)
            return True
        return launch_app(target)

    elif action_type == "powershell":
        command = payload.get("command", "")
        if command:
            try:
                subprocess.Popen(["powershell.exe", "-NoProfile", "-WindowStyle", "Hidden", "-Command", command])
                return True
            except Exception as e:
                print(f"[PowerShell Error] {e}")
                return False

    return False

# Carregar e Salvar Configuração
CONFIG_FILE = os.path.join(BASE_DIR, "config.json")

def load_config():
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return get_default_config()

def save_config(cfg):
    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2, ensure_ascii=False)
        return True
    except Exception as e:
        print(f"[Config Save Error] {e}")
        return False

def get_default_config():
    return {
        "device_name": "PhoneDesk Panel",
        "telemetry_mode": "workstation",
        "weather": {
            "city": "Juiz de Fora",
            "lat": -21.7642,
            "lon": -43.3496,
            "interval_min": 15
        },
        "buttons": [
            {
                "id": "explorer",
                "label": "Arquivos",
                "icon": "folder",
                "color": "#eab308",
                "action": "launch",
                "target": "explorer.exe"
            },
            {
                "id": "chrome",
                "label": "Navegador",
                "icon": "globe",
                "color": "#f97316",
                "action": "launch",
                "target": "chrome.exe"
            },
            {
                "id": "terminal",
                "label": "Terminal",
                "icon": "terminal",
                "color": "#475569",
                "action": "launch",
                "target": "wt.exe"
            },
            {
                "id": "lock_pc",
                "label": "Bloquear PC",
                "icon": "lock",
                "color": "#ef4444",
                "action": "lock",
                "target": ""
            }
        ]
    }

# PhoneBox original (agente instalado) como backend real de todas as funcionalidades.
# O PhoneDesk serve a interface própria na porta 8770 e repassa ao agente original
# (127.0.0.1:8765) as rotas que ele já implementa: configurações completas, estado,
# áudio, mídia, atalhos, agenda, feeds, etc.
import http.client

PHONEBOX_HOST = "127.0.0.1"
PHONEBOX_PORT = 8765

# Rotas que o telefone pode usar pela rede (espelha Lan.AllowedOverLan do PhoneBox original).
LAN_EXACT = {
    "/api/controls/spotify/state", "/api/controls/plex/state", "/api/controls/state", "/api/controls/listen",
    "/api/lyrics", "/api/wallpaper", "/api/dictate", "/api/steam/library", "/api/steam/cover", "/api/steam/launch",
    "/api/camera", "/api/spectrum", "/api/steam", "/api/steam/avatar", "/api/wake/macs",
}
LAN_PREFIX = (
    "/api/controls/spotify/action", "/api/controls/plex/action", "/api/controls/radio-logo", "/api/controls/command",
    "/api/controls/obs", "/api/state", "/api/config", "/api/art", "/api/media", "/api/volume", "/api/quotas",
    "/api/work", "/api/procs", "/api/run", "/api/apps", "/api/shortcuts/icon", "/api/console", "/api/widget",
    "/api/calendar", "/api/feeds", "/api/photos", "/api/docker", "/api/obs", "/api/discord", "/api/batteries",
    "/api/ha/states", "/api/ha/toggle", "/api/approvals", "/api/rules/pause", "/api/phone/battery", "/api/stream",
    "/api/trackpad", "/api/weather",
)
HOP_HEADERS = {"connection", "keep-alive", "transfer-encoding", "te", "trailer", "upgrade", "proxy-authorization",
               "proxy-authenticate", "host", "content-length"}


def lan_allowed(path):
    return path in LAN_EXACT or any(path == p or path.startswith(p + "/") for p in LAN_PREFIX)


SHORTCUT_PICKER_JS = r"""
<script>
(function () {
  'use strict';
  function el(tag, attrs, text) { var e = document.createElement(tag); for (var k in (attrs || {})) e.setAttribute(k, attrs[k]); if (text) e.textContent = text; return e; }
  function slug(s) { return s.toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 40) || 'app'; }
  function settingsCfg() { try { return cfg; } catch (_) { return null; } }
  function open() {
    var box = document.getElementById('pdApps');
    if (box) { box.remove(); return; }
    box = el('div', { id: 'pdApps', role: 'dialog', 'aria-label': 'Programas instalados' });
    box.style.cssText = 'position:fixed;right:20px;bottom:76px;width:min(440px,92vw);max-height:70vh;display:flex;flex-direction:column;background:#fff;border:1px solid #d6d9de;border-radius:12px;box-shadow:0 18px 48px rgba(15,23,42,.18);z-index:9999;font:14px system-ui,sans-serif;color:#0f172a';
    var head = el('div'); head.style.cssText = 'padding:14px 16px 10px;border-bottom:1px solid #eef0f3';
    head.appendChild(el('b', {}, 'Programas instalados neste computador'));
    var hint = el('div', {}, 'Escolha um programa para adicionar como atalho. Depois clique em Salvar.'); hint.style.cssText = 'color:#64748b;font-size:12.5px;margin-top:4px';
    head.appendChild(hint);
    var q = el('input', { type: 'search', placeholder: 'Buscar programa...', 'aria-label': 'Buscar programa' });
    q.style.cssText = 'margin-top:10px;width:100%;box-sizing:border-box;padding:8px 10px;border:1px solid #d6d9de;border-radius:8px;font:inherit';
    head.appendChild(q);
    var list = el('div'); list.style.cssText = 'overflow:auto;padding:6px';
    box.appendChild(head); box.appendChild(list); document.body.appendChild(box);
    list.textContent = 'Carregando...';
    fetch('/api/phonedesk/apps').then(function (r) { return r.json(); }).then(function (apps) {
      function render() {
        var c = settingsCfg();
        var term = q.value.trim().toLowerCase();
        list.textContent = '';
        var have = new Set(((c && c.shortcuts) || []).map(function (s) { return (s.cmd || '').toLowerCase(); }));
        apps.filter(function (a) { return !term || a.name.toLowerCase().indexOf(term) >= 0; }).forEach(function (a) {
          var added = have.has(a.path.toLowerCase());
          var row = el('button', { type: 'button' });
          row.style.cssText = 'display:flex;width:100%;align-items:center;justify-content:space-between;gap:10px;padding:9px 10px;border:0;border-radius:8px;background:transparent;font:inherit;text-align:left;cursor:pointer;transition:background .15s';
          row.onmouseenter = function () { row.style.background = '#f1f5f9'; }; row.onmouseleave = function () { row.style.background = 'transparent'; };
          row.appendChild(el('span', {}, a.name));
          var tag = el('span', {}, added ? 'Adicionado' : 'Adicionar'); tag.style.cssText = 'font-size:12px;font-weight:600;color:' + (added ? '#16a34a' : '#2563eb');
          row.appendChild(tag);
          row.disabled = added;
          row.onclick = function () {
            var c2 = settingsCfg();
            if (!c2) return;
            c2.shortcuts = c2.shortcuts || [];
            var id = slug(a.name), n = 2; while (c2.shortcuts.some(function (s) { return s.id === id; })) id = slug(a.name) + '-' + (n++);
            c2.shortcuts.push({ id: id, label: a.name, icon: '\uD83D\uDDA5\uFE0F', color: '#2563eb', cmd: a.path });
            try { renderDeck(); } catch (_) {}
            try { changed(); } catch (_) {}
            render();
          };
          list.appendChild(row);
        });
        if (!list.children.length) list.textContent = 'Nenhum programa encontrado.';
      }
      q.oninput = render; render(); q.focus();
    }).catch(function () { list.textContent = 'Nao foi possivel listar os programas.'; });
  }
  function mount() {
    var b = el('button', { type: 'button', id: 'pdAppsBtn' }, '+ Programas instalados');
    b.style.cssText = 'position:fixed;right:20px;bottom:24px;z-index:9998;padding:10px 16px;border-radius:999px;border:1px solid #1d4ed8;background:#2563eb;color:#fff;font:600 13.5px system-ui,sans-serif;cursor:pointer;box-shadow:0 6px 18px rgba(37,99,235,.28);transition:transform .12s,opacity .12s';
    b.onmousedown = function () { b.style.transform = 'scale(.96)'; }; b.onmouseup = function () { b.style.transform = ''; };
    b.onclick = open;
    document.body.appendChild(b);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount); else mount();
})();
</script>
"""


_weather_cache = {"key": None, "ts": 0.0, "data": None}
_weather_lock = threading.Lock()


def get_real_weather():
    import urllib.request
    lat, lon, city, unit = -21.7642, -43.3496, "Juiz de Fora", "celsius"
    try:
        with urllib.request.urlopen(f"http://{PHONEBOX_HOST}:{PHONEBOX_PORT}/api/config", timeout=3) as r:
            raw = json.loads(r.read().decode("utf-8"))
            w = (raw.get("config") or raw).get("weather") or {}
            if w.get("lat") is not None and w.get("lon") is not None:
                lat = float(w["lat"])
                lon = float(w["lon"])
            if w.get("city"):
                city = str(w["city"]).strip()
            if w.get("unit"):
                unit = str(w["unit"]).strip().lower()
    except Exception:
        pass

    if city.upper() == "JF":
        city = "Juiz de Fora"

    cache_key = (round(lat, 4), round(lon, 4), unit)
    now = time.time()
    with _weather_lock:
        if _weather_cache["data"] and _weather_cache["key"] == cache_key and (now - _weather_cache["ts"]) < 600:
            res = dict(_weather_cache["data"])
            res["city"] = city
            return res

    unit_param = "&temperature_unit=fahrenheit" if unit == "fahrenheit" else ""
    url = (
        f"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}"
        f"&current=temperature_2m,apparent_temperature,weather_code,is_day&timezone=auto{unit_param}"
    )
    try:
        with urllib.request.urlopen(url, timeout=8) as r:
            j = json.loads(r.read().decode("utf-8"))
            cur = j.get("current") or {}
            data = {
                "city": city,
                "temp": round(float(cur.get("temperature_2m", 0))),
                "feelsLike": round(float(cur.get("apparent_temperature", cur.get("temperature_2m", 0)))),
                "code": int(cur.get("weather_code", 0)),
                "isDay": bool(cur.get("is_day", 1)),
            }
            with _weather_lock:
                _weather_cache["key"] = cache_key
                _weather_cache["ts"] = now
                _weather_cache["data"] = data
            return data
    except Exception as e:
        with _weather_lock:
            if _weather_cache["data"]:
                return _weather_cache["data"]
        return {"city": city, "temp": None, "feelsLike": None, "code": 0, "isDay": True, "error": str(e)}


def get_installed_windows_apps():
    apps = []
    roots = [
        os.path.expandvars(r"%APPDATA%\Microsoft\Windows\Start Menu\Programs"),
        os.path.expandvars(r"%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs"),
        os.path.join(os.path.expanduser("~"), "Desktop"),
        os.path.expandvars(r"%PUBLIC%\Desktop"),
    ]
    skip = ("uninstall", "desinstalar", "remover", "help", "ajuda", "readme", "leia-me", "documentation")
    seen = set()
    for root in roots:
        if not os.path.isdir(root):
            continue
        for dirpath, _, filenames in os.walk(root):
            for f in filenames:
                if not f.lower().endswith((".lnk", ".url")):
                    continue
                name = os.path.splitext(f)[0].strip()
                if any(s in name.lower() for s in skip) or name.lower() in seen:
                    continue
                seen.add(name.lower())
                apps.append({"name": name, "path": os.path.join(dirpath, f)})
    apps.sort(key=lambda a: a["name"].lower())
    return apps


class DeskDockHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        msg = format % args
        if "/api/status" not in msg and "/api/state" not in msg and "/api/stream" not in msg:
            print(f"[HTTP] {msg}")

    def is_loopback(self):
        return self.client_address[0] in ("127.0.0.1", "::1")

    def send_json(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_bytes(code, body, "application/json; charset=utf-8")

    def send_bytes(self, code, body, ctype):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def read_body(self):
        length = int(self.headers.get("Content-Length", 0) or 0)
        return self.rfile.read(length) if length > 0 else b""

    def proxy(self, method, target, body=b"", inject=None):
        headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP_HEADERS and k.lower() != "origin"}
        if inject:
            headers["Accept-Encoding"] = "identity"
        conn = http.client.HTTPConnection(PHONEBOX_HOST, PHONEBOX_PORT, timeout=30)
        try:
            conn.request(method, target, body=body or None, headers=headers)
            resp = conn.getresponse()
        except OSError:
            conn.close()
            self.send_json(502, {"error": "Agente PhoneBox nao esta rodando em 127.0.0.1:8765"})
            return
        ctype = resp.getheader("Content-Type", "")
        if "text/event-stream" in ctype:
            self.send_response(resp.status)
            for k, v in resp.getheaders():
                if k.lower() not in HOP_HEADERS:
                    self.send_header(k, v)
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            try:
                while True:
                    chunk = resp.read1(4096)
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    self.wfile.flush()
            except OSError:
                pass
            finally:
                conn.close()
            return
        data = resp.read()
        conn.close()
        if inject and "text/html" in ctype:
            data = data.replace(b"</body>", inject.encode("utf-8") + b"</body>", 1)
        self.send_response(resp.status)
        for k, v in resp.getheaders():
            if k.lower() not in HOP_HEADERS:
                self.send_header(k, v)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        if method != "HEAD":
            self.wfile.write(data)

    def route_proxy(self, method, body=b""):
        """True quando a requisição foi tratada repassando ao PhoneBox original."""
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        if path in ("/settings", "/settings.html", "/config"):
            if not self.is_loopback():
                self.send_bytes(403, "<h3>As configuracoes abrem no navegador do computador.</h3>".encode("utf-8"),
                                "text/html; charset=utf-8")
                return True
            self.proxy("GET", "/" + (("?" + parsed.query) if parsed.query else ""), inject=SHORTCUT_PICKER_JS)
            return True
        if path.startswith("/panel/") or path == "/favicon.ico":
            self.proxy(method, self.path, body)
            return True
        if path.startswith("/api/") and not path.startswith("/api/phonedesk") and path not in ("/api/status", "/api/action", "/api/ping"):
            if self.is_loopback() or lan_allowed(path):
                self.proxy(method, self.path, body)
            else:
                self.send_json(403, {"error": "Rota disponivel apenas no computador"})
            return True
        return False

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_PUT(self):
        if not self.route_proxy("PUT", self.read_body()):
            self.send_json(404, {"error": "Not Found"})

    def do_DELETE(self):
        if not self.route_proxy("DELETE", self.read_body()):
            self.send_json(404, {"error": "Not Found"})

    def do_GET(self):
        try:
            if self.route_proxy("GET"):
                return
            self.handle_local_get()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        except Exception as e:
            import traceback
            traceback.print_exc()
            self.send_json(500, {"error": str(e)})

    def handle_local_get(self):
        path = urllib.parse.urlparse(self.path).path
        if path == "/api/status":
            cfg = load_config()
            self.send_json(200, {
                "status": "online",
                "version": VERSION,
                "hostname": socket.gethostname(),
                "ips": get_local_ips(),
                "cpu_percent": get_windows_cpu(),
                "memory": get_windows_memory(),
                "disk": get_windows_disk(),
                "uptime": get_windows_uptime(),
                "telemetry_mode": cfg.get("telemetry_mode", "workstation"),
                "timestamp": int(time.time()),
            })
            return
        if path == "/api/ping":
            self.send_bytes(200, b"pong", "text/plain")
            return
        if path == "/api/phonedesk/weather":
            self.send_json(200, get_real_weather())
            return
        if path == "/api/phonedesk/apps":
            if not self.is_loopback():
                self.send_json(403, {"error": "Rota disponivel apenas no computador"})
                return
            self.send_json(200, get_installed_windows_apps())
            return
        if path == "/api/phonedesk/version":
            self.send_json(200, {"version": str(int(os.path.getmtime(os.path.join(BASE_DIR, "index.html"))))})
            return
        static = {
            "/": ("index.html", "text/html; charset=utf-8"),
            "/index.html": ("index.html", "text/html; charset=utf-8"),
            "/manifest.json": ("manifest.json", "application/manifest+json"),
            "/sw.js": ("sw.js", "application/javascript"),
            "/icon.svg": ("icon.svg", "image/svg+xml"),
            "/app.js": ("app.js", "application/javascript"),
            "/style.css": ("style.css", "text/css"),
        }
        if path in static:
            name, ctype = static[path]
            fp = os.path.join(BASE_DIR, name)
            if os.path.isfile(fp):
                with open(fp, "rb") as f:
                    self.send_bytes(200, f.read(), ctype)
                return
        self.send_bytes(404, b"Not Found", "text/plain")

    def do_POST(self):
        try:
            body = self.read_body()
            if self.route_proxy("POST", body):
                return
            path = urllib.parse.urlparse(self.path).path
            try:
                payload = json.loads(body.decode("utf-8")) if body else {}
            except Exception:
                payload = {}
            if path == "/api/action":
                success = execute_action(payload.get("action"), payload.get("payload", {}))
                self.send_json(200 if success else 400, {"ok": bool(success)})
                return
            self.send_json(404, {"error": "Not Found"})
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        except Exception as e:
            import traceback
            traceback.print_exc()
            self.send_json(500, {"ok": False, "error": str(e)})


class PhoneDeskServer(ThreadingHTTPServer):
    # No Windows, SO_REUSEADDR deixa dois processos ouvirem a mesma porta e as
    # requisições caem no processo antigo. Porta exclusiva.
    allow_reuse_address = False
    daemon_threads = True


# Serviço de Descoberta Automática via UDP Broadcast
def udp_discovery_loop():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    try:
        sock.bind(("", UDP_PORT))
    except Exception as e:
        print(f"[UDP Warning] Could not bind UDP {UDP_PORT}: {e}")
        return

    while True:
        try:
            data, addr = sock.recvfrom(1024)
            msg = data.decode("utf-8", errors="ignore").strip()
            if msg == "deskdock_discover" or "deskdock" in msg:
                reply = json.dumps({
                    "service": "deskdock",
                    "hostname": socket.gethostname(),
                    "port": PORT,
                    "ips": get_local_ips(),
                    "version": VERSION
                }).encode("utf-8")
                sock.sendto(reply, addr)
        except Exception:
            pass

def adb_reverse_loop():
    """Mantém o túnel adb reverse tcp:8770 ativo se houver celular via USB."""
    adb_exe = shutil.which("adb")
    if not adb_exe:
        candidate = os.path.expandvars(r"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe")
        if os.path.isfile(candidate):
            adb_exe = candidate
    if not adb_exe:
        return
    while True:
        try:
            subprocess.run(
                [adb_exe, "reverse", f"tcp:{PORT}", f"tcp:{PORT}"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
            )
        except Exception:
            pass
        time.sleep(25)


def main():
    print(f"=== DeskDock PC Agent v{VERSION} ===")
    ips = get_local_ips()
    print("Enderecos disponiveis na rede local:")
    for ip in ips:
        print(f" -> http://{ip}:{PORT}")
    
    # Iniciar thread de descoberta UDP e túnel USB ADB
    t_udp = threading.Thread(target=udp_discovery_loop, daemon=True)
    t_udp.start()
    t_adb = threading.Thread(target=adb_reverse_loop, daemon=True)
    t_adb.start()

    # Iniciar servidor HTTP multithreaded
    server = PhoneDeskServer(("0.0.0.0", PORT), DeskDockHandler)
    print(f"[DeskDock] Servidor ativo em 0.0.0.0:{PORT}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nDesligando DeskDock...")
        server.server_close()

if __name__ == "__main__":
    main()
