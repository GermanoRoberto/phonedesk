# PhoneDesk v1.0.0 — Guia de Arquitetura e Implementação (macOS & Linux)

Este documento reúne todas as especificações técnicas, comportamentos, integrações recentes e os passos práticos para compilar, executar e testar o **PhoneDesk** no **macOS** (e preparar o ambiente para **Linux**).

---

## 1. Visão Geral da Arquitetura

O PhoneDesk é um ecossistema leve e desacoplado que transforma qualquer smartphone Android (especialmente modelos antigos) em um **Smart Desk Panel** (relógio de mesa inteligente, clima, agenda e monitor de hardware) e **Stream Deck tátil** (atalhos rápidos, controles de mídia e automações).

```
                      +------------------------------------------+
                      |           Dispositivo Android            |
                      |  - App Nativo PhoneDesk (Kiosk/WebView)  |
                      |  - Ou Navegador Web em Tela Cheia        |
                      +------------------------------------------+
                                           ▲
                                           │ HTTP / SSE / WebSocket
                                           │ Porta padrão: 5173
                                           ▼
+---------------------------------------------------------------------------------+
|                       PhoneDesk Agent (.NET 10 Cross-Platform)                  |
|                                                                                 |
|  [ Kestrel Web Server ]                                                         |
|    ├── /panel/                -> Painel Web (HTML5/CSS3/Vanilla JS)             |
|    ├── /settings              -> Estúdio de Customização de Atalhos             |
|    ├── /api/state/stream      -> Server-Sent Events (SSE) em tempo real (1s)    |
|    ├── /api/phonedesk/version -> Polling de auto-reload inteligente             |
|    ├── /api/shortcuts/icon    -> Extração dinâmica de ícones nativos/upload     |
|    └── /api/action/*          -> Comandos de mídia, atalhos e volume            |
|                                                                                 |
|  [ Provedores de Plataforma ]                                                   |
|    ├── Windows : WinForms Tray, WinRT Media, CoreAudio, NAudio, Process.Icon    |
|    ├── macOS   : LaunchAgent, Mach Kernel, sysctl, vm_stat, osascript, sips     |
|    └── Linux   : Systemd User, /proc/stat, /proc/meminfo, wpctl/pactl, .desktop  |
+---------------------------------------------------------------------------------+
```

---

## 2. Resumo das Últimas Funcionalidades Implementadas (v1.0.0)

### 2.1. Extração e Renderização Real de Ícones de Aplicativos
- **Problema anterior:** Atalhos com comandos para executáveis reais exibiam apenas emojis genéricos tanto na Tela 2 (Stream Deck) quanto no Estúdio de Configurações.
- **Solução implementada:**
  - Endpoint `/api/shortcuts/icon?id={id}` com fluxo de precedência:
    1. **Upload Personalizado:** Se o usuário fez upload de imagem personalizada (PNG, WEBP, SVG até 512 KB), ela é servida.
    2. **Ícone Nativo do Aplicativo:** O agente resolve o caminho do programa no disco e extrai o ícone real.
    3. **Emoji:** Fallback visual padrão caso o comando seja uma URL, comando interno `builtin:` ou o arquivo não possua ícone.
  - **No macOS (`UnixProc.cs`):**
    - Se o comando apontar para um `.app` (ex.: `/Applications/Visual Studio Code.app`), o agente localiza o arquivo `.icns` dentro de `Contents/Resources/` e o converte em tempo de execução para PNG usando a ferramenta nativa do macOS `sips`:
      ```bash
      sips -s format png --resampleWidth 64 app.icns --out /tmp/phonedesk-icon.png
      ```
  - **No Linux (`UnixProc.cs`):**
    - Varre arquivos `.desktop` em `/usr/share/applications`, `/var/lib/flatpak/exports/share/applications` e `~/.local/share/applications`.
    - Mapeia o executável para o tema de ícones do sistema (`/usr/share/icons/hicolor`, `Adwaita`, `/usr/share/pixmaps`).
  - **Na Interface Web (`panel/index.html` e `settings.html`):**
    - Os botões carregam a tag `<img>` assincronamente apontando para `/api/shortcuts/icon?id={sc.id}`. Se carregar com sucesso, esconde o emoji; se falhar (`onerror`), o emoji permanece visível.

---

### 2.2. Integração com Calendário Outlook / Microsoft 365 (Feed ICS)
- **Como funciona:** Suporte nativo a URLs de feeds iCalendar (`.ics`), inclusive links públicos e anônimos do Outlook Web / Exchange corporativo.
- **Sem OAuth:** Não exige criação de aplicativo no Azure AD nem chaves de API pagas.
- **Comportamento na Tela 1 (StandBy):**
  - Mostra o **próximo compromisso futuro mais próximo** (ou o evento que está ocorrendo no exato momento).
  - Formato claro: `Seg, 30/11 às 16:00 • Consultor da ISO` acompanhado do ícone da plataforma de reunião (Teams, Meet, Zoom).
  - Se a URL do calendário estiver vazia ou não houver eventos nos próximos 7 dias, a linha é completamente escondida (`display: none`), sem deixar espaços vazios.
- **Comportamento na Tela 5 (Agenda Completa):**
  - Lista todos os compromissos em ordem cronológica com botão para ingressar diretamente na videoconferência.

---

### 2.3. Alertas Meteorológicos com Cores Dinâmicas de Severidade
- **Classificação de Severidade:**
  - `danger` (Perigo / Tempestade severa / Alagamento): Vermelho pulsante (`#ef4444`).
  - `warning` (Aviso / Chuva forte / Ventos): Laranja / Âmbar (`#f59e0b`).
  - `info` (Aviso informativo / Variação térmica): Ciano (`#06b6d4`).
- **Hierarquia Visual na Tela 1:**
  1. Hora e Minutos (Relógio grande de cabeceira).
  2. Temperatura, Condição Climática e Umidade.
  3. Alerta Meteorológico (com badge na cor da severidade).
  4. Próximo compromisso da Agenda.

---

### 2.4. Modo Relógio de Mesa Eco StandBy (Tolerância de 3 Minutos)
- **Cenário:** O que acontece quando o computador desliga, suspende ou hiberna?
- **Comportamento Implementado:**
  - O aplicativo Android **não fecha nem trava** (o WebView mantém a tela ativa graças à flag `FLAG_KEEP_SCREEN_ON`).
  - O JavaScript monitora o fluxo de dados em tempo real (SSE). Se o computador ficar offline por **3 minutos consecutivos (180 segundos)**:
    1. **Trava de Tela:** A navegação é bloqueada exclusivamente na **Tela 1 (Relógio de Mesa)**. Gestos de arraste (swipe) para as telas 2, 3, 4 e 5 são desativados.
    2. **Ocultação de Elementos:** Os pontos de paginação (pager dots) e o botão de configurações somem suavemente.
    3. **Dimming Automático (Ultra-Eco):** A tela reduz o brilho para ~20% com fundo preto OLED absoluto (`#000000`), economizando bateria e prevenindo *burn-in*.
    4. **Touch Wake:** Ao tocar na tela escura, o brilho volta a 100% por 15 segundos para conferência de horário e volta a escurecer.
    5. **Auto-Restauração:** Assim que o computador acorda ou reinicia e o agente responde na rede, o painel sai do modo eco, restaura o brilho original e reabilita todas as telas automaticamente.

---

## 3. Guia de Implementação e Execução no macOS

### 3.1. Pré-requisitos
- **macOS** 12 (Monterey), 13 (Ventura), 14 (Sonoma) ou 15 (Sequoia).
- **.NET 10 SDK** instalado:
  - Via Homebrew:
    ```bash
    brew install dotnet-sdk
    ```
  - Ou baixando o instalador oficial `.pkg` (Apple Silicon ARM64 ou Intel x64) no site da Microsoft.

---

### 3.2. Clonando e Executando em Modo de Desenvolvimento
No terminal do seu Mac:
```bash
git clone https://github.com/GermanoRoberto/phonedesk.git
cd phonedesk/agent/PhoneDesk.Agent
dotnet run -f net10.0
```
O agente iniciará e você verá a saída no console:
```text
PhoneDesk agent v1.0.0 (macos-arm64)
Listening on http://localhost:5173 and http://0.0.0.0:5173
Serving web panel from: ../../panel
```

Abra no Safari ou Chrome do Mac:
- **Painel:** `http://localhost:5173/panel/`
- **Configurações:** `http://localhost:5173/settings`

---

### 3.3. Compilação e Publicação Binária Nativa
Para gerar o executável final otimizado e independente:

- **Para Mac com chip Apple Silicon (M1, M2, M3, M4):**
  ```bash
  dotnet publish -f net10.0 -r osx-arm64 --self-contained -c Release -o dist/macos-arm64
  ```

- **Para Mac com processador Intel:**
  ```bash
  dotnet publish -f net10.0 -r osx-x64 --self-contained -c Release -o dist/macos-x64
  ```

O executável gerado será `PhoneDeskAgent`. Para rodá-lo:
```bash
chmod +x dist/macos-arm64/PhoneDeskAgent
./dist/macos-arm64/PhoneDeskAgent
```

---

### 3.4. Localização dos Arquivos de Configuração no macOS
O `AppPaths.cs` segue as diretrizes da Apple:
- **Configuração do Usuário:**
  `~/Library/Application Support/PhoneDesk/config.json`
- **Logs de Execução:**
  `~/Library/Application Support/PhoneDesk/agent.log`
- **Configuração Padrão Inicial:**
  O arquivo embutido `config.default.macos.json` contém atalhos específicos para macOS (Terminal, Safari, VS Code, Music).

---

### 3.5. Como o macOS é Suportado no Código C#
Todas as chamadas de baixo nível para macOS estão centralizadas no diretório `agent/PhoneDesk.Agent/Unix/`:

1. **Telemetria de CPU (`UnixSystem.cs`):**
   - Utiliza a chamada de Mach Kernel `host_statistics` com flavor `HOST_CPU_LOAD_INFO` da biblioteca `/usr/lib/libSystem.dylib` para medir tempos de ticks `user`, `system`, `idle` e `nice`.
   - Lê o nome do processador via `sysctl -n machdep.cpu.brand_string` (ex.: `Apple M1 Pro`).

2. **Memória RAM (`UnixSystem.cs`):**
   - Tamanho total via `sysctl -n hw.memsize`.
   - Uso detalhado via `vm_stat` (páginas anônimas, purgaveis, ativas, wired e occupied by compressor) multiplicado pelo page size da arquitetura (16.384 bytes no Apple Silicon, 4.096 bytes no Intel).

3. **Controle de Volume e Mídia (`Unix/AudioControl.cs` e `Unix/MediaMonitor.cs`):**
   - Leitura e ajuste de volume via AppleScript nativo (`osascript -e 'set volume output volume X'`).
   - Leitura de faixas tocando no Spotify / Apple Music via AppleScript.

4. **Extração de Ícones (`Unix/UnixProc.cs`):**
   - Localiza o `.icns` dentro de `Contents/Resources` do aplicativo `.app`.
   - Converte de forma ultra-rápida para PNG usando a ferramenta de linha de comando nativa da Apple `sips`.

5. **Execução em Segundo Plano no Início da Sessão (LaunchAgent):**
   - Criação de um arquivo `~/Library/LaunchAgents/com.phonedesk.agent.plist`:
     ```xml
     <?xml version="1.0" encoding="UTF-8"?>
     <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
     <plist version="1.0">
     <dict>
         <key>Label</key>
         <string>com.phonedesk.agent</string>
         <key>ProgramArguments</key>
         <array>
             <string>/Applications/PhoneDesk/PhoneDeskAgent</string>
         </array>
         <key>RunAtLoad</key>
         <true/>
         <key>KeepAlive</key>
         <true/>
     </dict>
     </plist>
     ```

---

## 4. Guia de Implementação e Execução no Linux

### 4.1. Pré-requisitos
- Ubuntu 22.04+, Debian 12+, Fedora 38+, Arch Linux ou qualquer distribuição moderna.
- .NET 10 SDK (`sudo apt install dotnet-sdk-10.0` ou pacote correspondente da distro).
- Servidor de Áudio: PipeWire (`wireplumber` / `wpctl`) ou PulseAudio (`pactl`).

---

### 4.2. Compilação e Execução
```bash
git clone https://github.com/GermanoRoberto/phonedesk.git
cd phonedesk/agent/PhoneDesk.Agent
dotnet run -f net10.0
```

Publicação estática autocontida para Linux:
```bash
dotnet publish -f net10.0 -r linux-x64 --self-contained -c Release -o dist/linux-x64
```

---

### 4.3. Caminhos no Linux (XDG Base Directory)
- **Configuração:** `~/.config/PhoneDesk/config.json`
- **Dados e Logs:** `~/.local/share/PhoneDesk/agent.log`
- **Configuração Padrão Inicial:** `config.default.linux.json`

---

### 4.4. Integrações Nativas Linux
- **Telemetria:**
  - CPU: `/proc/stat`
  - RAM: `/proc/meminfo` (`MemTotal`, `MemAvailable`)
  - Temperatura do Processador: `/sys/class/hwmon/` (módulos `k10temp`, `zenpower`, `coretemp`, `cpu_thermal`)
- **Controle de Áudio:**
  - PipeWire: `wpctl get-volume @DEFAULT_AUDIO_SINK@` e `wpctl set-volume @DEFAULT_AUDIO_SINK@ X%`
  - PulseAudio: `pactl get-sink-volume @DEFAULT_SINK@`
- **Ícones:** Varredura em `/usr/share/applications` para mapear arquivos `.desktop` e leitura dos temas hicolor/Adwaita.
- **Serviço do Usuário (Systemd):**
  Criar em `~/.config/systemd/user/phonedesk.service`:
  ```ini
  [Unit]
  Description=PhoneDesk Desktop Agent
  After=network.target sound.target

  [Service]
  ExecStart=%h/.local/bin/PhoneDeskAgent
  Restart=on-failure

  [Install]
  WantedBy=default.target
  ```
  Ativar com:
  ```bash
  systemctl --user enable --now phonedesk
  ```

---

## 5. Conectando o Celular Android ao Mac ou Linux

1. **Na mesma rede Wi-Fi:**
   - Descubra o IP local do seu Mac (`ipconfig getifaddr en0` ou em Ajustes de Rede).
   - No celular Android, aponte o navegador ou o aplicativo PhoneDesk para:
     `http://IP_DO_SEU_MAC:5173/panel/`
   - O QR Code nas Configurações (`/settings`) gera esse endereço automaticamente.

2. **Via Cabo USB com ADB (Sem latência e sem depender do Wi-Fi):**
   - Com o celular conectado via USB e a Depuração USB ativada:
     ```bash
     adb reverse tcp:5173 tcp:5173
     ```
   - No celular, basta acessar `http://localhost:5173/panel/`.

---

## 6. Checklist de Validação no Mac

- [ ] Clonar repositório e executar `dotnet run -f net10.0`.
- [ ] Acessar `http://localhost:5173/panel/` no navegador do Mac.
- [ ] Verificar se a telemetria de CPU (Mach kernel) e RAM (`vm_stat`) aparecem em tempo real na Tela 3.
- [ ] Testar atalhos na Tela 2 com aplicativos instalados no Mac (ex.: VS Code, Safari) e validar se o ícone do `.app` foi renderizado via `sips`.
- [ ] Adicionar feed ICS do calendário e verificar se o próximo compromisso é exibido na Tela 1.
- [ ] Testar o Modo Eco StandBy: parar o agente no Mac (`Ctrl+C`), esperar 3 minutos (ou simular no console) e verificar o bloqueio de navegação e dimming de tela no celular.
