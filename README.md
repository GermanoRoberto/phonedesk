# PhoneDesk

Smart Desk Panel & Control Deck de código aberto para transformar qualquer smartphone ou tablet em um painel interativo de mesa (iOS StandBy + Stream Deck + Mídia & Volume + Hardware PC + Agenda & Feeds), com agente autônomo em **.NET 10** (baseado no projeto open-source **PhoneBox**) e interface web responsiva sem dependências externas.

> **Crédito & Origem:** O agente desktop (`agent/PhoneDesk.Agent`) foi construído sobre a base técnica do projeto open-source [PhoneBox](https://github.com/ersinkoc/phonebox), evoluído com a marca **PhoneDesk**, servidor web LAN nativo na porta `8770`, descoberta automática de aplicativos instalados no Windows (`+ Programas instalados`), e interface própria de 5 telas (`index.html`). Veja [THIRD-PARTY-NOTICES.txt](agent/PhoneDesk.Agent/THIRD-PARTY-NOTICES.txt).

---

## ✨ Funcionalidades

### 1. Tela 1 — StandBy iOS
- **Relógio gigante verde-menta** (`#86efac`) e data completa em português.
- **Clima real** (Open-Meteo, endpoint nativo `/api/phonedesk/weather`) com ícones vetoriais SVG (zero emojis).
- **Próximo compromisso real** da agenda configurada (`.ics`), indicado pelo ponto verde brilhante.

### 2. Tela 2 — Stream Deck
- Squircles em Liquid Glass carregados dinamicamente da configuração salva (`/api/config`).
- Disparo real de aplicativos, URLs, atalhos de teclado e ações do sistema (`POST /api/run/{id}`).

### 3. Tela 3 — Mídia & Volume do Windows
- Controle real de reprodução via Windows SMTC (`Play/Pause`, `Anterior`, `Próxima`), capa do álbum ampliada (`/api/art`), artista/faixa/álbum e barra de progresso em tempo real.
- Controle real de áudio master via CoreAudio / WASAPI (`VOL −`, `VOL +`, `MUDO` e slider tátil com nome do dispositivo ativo).

### 4. Tela 4 — Hardware do PC
- Telemetria real de CPU, RAM, Disco, Ping e Uptime.
- Perfil **Workstation** padrão (sem dados gamers desnecessários) e perfil **Gamer** opcional com GPU/VRAM e taxas de rede reais.

### 5. Tela 5 — Agenda & Feeds RSS
- Próximos eventos de calendários `.ics` (`/api/calendar`).
- Manchetes de feeds RSS/Atom (`/api/feeds`) com abertura no navegador do computador ao tocar.

### 6. Configurações no Navegador do PC (`PhoneDesk Settings`)
- Acesse no navegador do computador:
  - `http://localhost:8770/settings` (ou `http://127.0.0.1:8765/`)
- O acesso às configurações é **bloqueado (`403`)** a partir de outros dispositivos na rede LAN, funcionando apenas localmente no PC.
- Inclui **editor visual completo** (páginas, atalhos, regras, calendários, feeds, aparência, clima) e o seletor **`+ Programas instalados`** para adicionar programas detectados no Windows em um clique.

---

## 🚀 Como Compilar e Executar

### Requisitos
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (ou execute com `dotnet run`).

### Rodar em desenvolvimento
```bash
dotnet run --project agent/PhoneDesk.Agent/PhoneDesk.Agent.csproj -f net10.0-windows10.0.19041.0
```

### Compilar em Release
```bash
dotnet build agent/PhoneDesk.Agent/PhoneDesk.Agent.csproj -c Release -f net10.0-windows10.0.19041.0
```

Ao iniciar, o `PhoneDeskAgent`:
- Abre o painel web na LAN em `http://0.0.0.0:8770/` (para abrir no navegador ou WebView Kiosk do celular/tablet).
- Serve **PhoneDesk Settings** apenas localmente em `http://localhost:8770/settings` e `http://127.0.0.1:8765/`.
- Armazena suas configurações pessoais em `%APPDATA%\PhoneDesk\config.json` (migrando automaticamente `%APPDATA%\PhoneBox\config.json` na primeira execução, se existir).

---

## 🏗️ Estrutura para Contribuições e Melhorias

```text
├── agent/PhoneDesk.Agent/   # Código-fonte completo do agente (.NET 10 / C# + PhoneDesk Settings HTML)
├── panel/                   # Módulos base do preview ao vivo das páginas e i18n
├── index.html               # Interface web principal de 5 telas do PhoneDesk (Liquid Glass / StandBy)
├── sw.js                    # Service Worker (network-first) para PWA / Kiosk
├── manifest.json            # Manifesto PWA
├── icon.svg                 # Ícone SVG do PhoneDesk
├── config.json              # Exemplo genérico de configuração inicial (sem dados pessoais)
└── README.md
```

Qualquer pessoa pode clonar, alterar os endpoints em `agent/PhoneDesk.Agent/Program.cs`, personalizar `agent/PhoneDesk.Agent/settings.html` ou evoluir a interface em `index.html` e recompilar com um único comando `dotnet build`.

---

## 📄 Licença
MIT License (veja [agent/PhoneDesk.Agent/THIRD-PARTY-NOTICES.txt](agent/PhoneDesk.Agent/THIRD-PARTY-NOTICES.txt) para créditos de código aberto e bibliotecas de terceiros).
