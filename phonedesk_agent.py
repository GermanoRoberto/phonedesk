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
    
    SANKHYA_LNK = r"C:\Users\TI Camilo\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\apps do Chrome\SANKHYA.lnk"
    OPS_LNK = r"C:\Users\TI Camilo\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\apps do Chrome\Camilo dos Santos   Operations Center.lnk"
    CHROME_EXE = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
    ANTIGRAVITY = r"C:\Users\TI Camilo\AppData\Local\Programs\Antigravity\Antigravity.exe"
    TEAMVIEWER = r"C:\Program Files\TeamViewer\TeamViewer.exe"
    TEAMVIEWER_X86 = r"C:\Program Files (x86)\TeamViewer\TeamViewer.exe"
    OUTLOOK = r"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE"
    SPOTIFY = r"C:\Users\TI Camilo\AppData\Local\Microsoft\WindowsApps\Spotify.exe"

    try:
        if app_key == "sankhya":
            if os.path.exists(SANKHYA_LNK):
                os.startfile(SANKHYA_LNK)
                print(f"[Launcher] Aberto com sucesso via atalho: {SANKHYA_LNK}")
                return True
            subprocess.Popen([CHROME_EXE, "--app-id=ogolepfkgjklanfdnhbcimgjaaimband"])
            return True

        elif app_key == "ops_center":
            if os.path.exists(OPS_LNK):
                os.startfile(OPS_LNK)
                print(f"[Launcher] Aberto com sucesso via atalho: {OPS_LNK}")
                return True
            subprocess.Popen([CHROME_EXE, "--app-id=faflnifmifdkmadpfdanheikmknjbbcg"])
            return True

        elif app_key == "antigravity":
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
        "device_name": "Galaxy A04e Desk Panel",
        "telemetry_mode": "workstation",
        "standby_status": "● Juiz de Fora • Modo Trabalho",
        "weather": {
            "city": "Juiz de Fora",
            "lat": -21.7642,
            "lon": -43.3496,
            "interval_min": 15
        },
        "buttons": [
            {
                "id": "antigravity",
                "label": "Antigravity",
                "icon": "rocket",
                "color": "#3b82f6",
                "action": "launch",
                "target": r"C:\Users\TI Camilo\AppData\Local\Programs\Antigravity\Antigravity.exe"
            },
            {
                "id": "operations",
                "label": "Ops Center",
                "icon": "building",
                "color": "#0ea5e9",
                "action": "launch",
                "target": r"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe --app-id=faflnifmifdkmadpfdanheikmknjbbcg"
            },
            {
                "id": "sankhya",
                "label": "Sankhya ERP",
                "icon": "trending-up",
                "color": "#10b981",
                "action": "launch",
                "target": r"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe --app-id=kjikbbkddkggblglgcljlnffmbbjkllo"
            },
            {
                "id": "teamviewer",
                "label": "TeamViewer",
                "icon": "monitor",
                "color": "#2563eb",
                "action": "launch",
                "target": r"C:\Program Files\TeamViewer\TeamViewer.exe"
            },
            {
                "id": "sandbox",
                "label": "Win Sandbox",
                "icon": "shield",
                "color": "#6366f1",
                "action": "powershell",
                "command": "Start-Process WindowsSandbox.exe"
            },
            {
                "id": "outlook",
                "label": "Outlook",
                "icon": "mail",
                "color": "#0284c7",
                "action": "launch",
                "target": "outlook.exe"
            },
            {
                "id": "whatsapp",
                "label": "WhatsApp",
                "icon": "message-circle",
                "color": "#22c55e",
                "action": "powershell",
                "command": "explorer.exe shell:AppsFolder\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"
            },
            {
                "id": "whatsapp_beta",
                "label": "WhatsApp Beta",
                "icon": "smartphone",
                "color": "#16a34a",
                "action": "powershell",
                "command": "explorer.exe shell:AppsFolder\\5319275A.WhatsAppDesktopBeta_cv1g1gvanyjgm!App"
            },
            {
                "id": "spotify",
                "label": "Spotify",
                "icon": "headphones",
                "color": "#10b981",
                "action": "powershell",
                "command": "explorer.exe shell:AppsFolder\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"
            },
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
                "target": r"C:\Program Files\Google\Chrome\Application\chrome.exe"
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

class DeskDockHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        # Loga se não for polling repetitivo
        msg = format % args
        if "/api/status" not in msg:
            print(f"[HTTP] {msg}")

    def send_cors(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")

    def do_OPTIONS(self):
        self.send_response(200)
        self.send_cors()
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        if path == "/api/status":
            mem = get_windows_memory()
            cpu = get_windows_cpu()
            disk = get_windows_disk()
            uptime = get_windows_uptime()
            cfg = load_config()
            data = {
                "status": "online",
                "version": VERSION,
                "hostname": socket.gethostname(),
                "ips": get_local_ips(),
                "cpu_percent": cpu,
                "memory": mem,
                "disk": disk,
                "uptime": uptime,
                "telemetry_mode": cfg.get("telemetry_mode", "workstation"),
                "timestamp": int(time.time())
            }
            res = json.dumps(data).encode("utf-8")
            self.send_response(200)
            self.send_cors()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(res)))
            self.end_headers()
            self.wfile.write(res)
            return

        elif path == "/api/config":
            cfg = load_config()
            res = json.dumps(cfg).encode("utf-8")
            self.send_response(200)
            self.send_cors()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(res)))
            self.end_headers()
            self.wfile.write(res)
            return

        elif path == "/api/ping":
            self.send_response(200)
            self.send_cors()
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"pong")
            return

        # Arquivos estáticos (PWA e Settings)
        if path in ("/", "/index.html"):
            file_path = os.path.join(BASE_DIR, "index.html")
            content_type = "text/html; charset=utf-8"
        elif path in ("/settings", "/settings.html", "/config"):
            file_path = os.path.join(BASE_DIR, "settings.html")
            content_type = "text/html; charset=utf-8"
        elif path == "/manifest.json":
            file_path = os.path.join(BASE_DIR, "manifest.json")
            content_type = "application/manifest+json"
        elif path == "/sw.js":
            file_path = os.path.join(BASE_DIR, "sw.js")
            content_type = "application/javascript"
        else:
            rel_path = path.lstrip("/").replace("/", os.sep)
            file_path = os.path.join(BASE_DIR, rel_path)
            if path.endswith(".css"):
                content_type = "text/css"
            elif path.endswith(".js"):
                content_type = "application/javascript"
            elif path.endswith(".json"):
                content_type = "application/json"
            elif path.endswith(".svg"):
                content_type = "image/svg+xml"
            elif path.endswith(".png"):
                content_type = "image/png"
            else:
                content_type = "text/plain"

        if os.path.exists(file_path) and os.path.isfile(file_path):
            with open(file_path, "rb") as f:
                content = f.read()
            self.send_response(200)
            self.send_cors()
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
        else:
            self.send_response(404)
            self.send_cors()
            self.end_headers()
            self.wfile.write(b"Not Found")

    def do_POST(self):
        try:
            parsed = urllib.parse.urlparse(self.path)
            path = parsed.path

            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length) if length > 0 else b"{}"
            try:
                payload = json.loads(body.decode("utf-8"))
            except Exception:
                payload = {}

            if path == "/api/action":
                action_type = payload.get("action")
                data = payload.get("payload", {})
                success = execute_action(action_type, data)
                res = json.dumps({"ok": bool(success)}).encode("utf-8")
                self.send_response(200 if success else 400)
                self.send_cors()
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(res)))
                self.end_headers()
                self.wfile.write(res)
                return

            elif path == "/api/config":
                saved = save_config(payload)
                res = json.dumps({"ok": bool(saved)}).encode("utf-8")
                self.send_response(200 if saved else 500)
                self.send_cors()
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(res)))
                self.end_headers()
                self.wfile.write(res)
                return

            self.send_response(404)
            self.send_cors()
            self.end_headers()
        except Exception as e:
            print(f"[POST Exception] {e}")
            err_res = json.dumps({"ok": False, "error": str(e)}).encode("utf-8")
            self.send_response(500)
            self.send_cors()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(err_res)))
            self.end_headers()
            self.wfile.write(err_res)

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

def main():
    print(f"=== DeskDock PC Agent v{VERSION} ===")
    ips = get_local_ips()
    print("Enderecos disponiveis na rede local:")
    for ip in ips:
        print(f" -> http://{ip}:{PORT}")
    
    # Iniciar thread de descoberta UDP
    t_udp = threading.Thread(target=udp_discovery_loop, daemon=True)
    t_udp.start()

    # Iniciar servidor HTTP multithreaded
    server = ThreadingHTTPServer(("0.0.0.0", PORT), DeskDockHandler)
    print(f"[DeskDock] Servidor ativo em 0.0.0.0:{PORT}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nDesligando DeskDock...")
        server.server_close()

if __name__ == "__main__":
    main()
