# PhoneDesk 📱🖥️

Transforme qualquer smartphone (Android ou iOS) em um **Smart Desk Panel & Control Deck** de alta fidelidade para o seu computador (Windows / Linux / macOS), combinando o design icônico do **iOS StandBy** com a versatilidade de um **Stream Deck com Liquid Glass**.

---

## ✨ Funcionalidades Principais

### 1. 🕒 Modo Descanso (iOS StandBy)
- **Relógio Gigante em Verde Menta** (Fonte 900 de alto impacto).
- **Data Completa em Português** e indicador de expediente/alarme.
- **Clima e Previsão em Tempo Real** com ícone vetorial solar de alto contraste.
- **OLED / AMOLED Ready:** Fundo preto absoluto (`#000000`) para economia de energia e contraste perfeito em qualquer ângulo de visão da mesa.

### 2. 🎛️ Stream Deck (Controles & Aplicativos)
- **Botões Grandes (Squircles 72px):** Visual imediato a 60cm de distância da mesa.
- **Ícones Oficiais em Vetor SVG:** Spotify, Google Chrome, WhatsApp, Outlook, TeamViewer, Windows Sandbox, ERP Sankhya, Antigravity, Explorer.
- **Ações Instantâneas no PC:** Abre aplicativos, PWAs do Chrome, janelas do Explorer e bloqueia a estação de trabalho (`Win + L`).

### 3. ⌨️ Suporte Completo a Teclas de Atalho (Hotkeys & Macros)
- Dispara qualquer atalho do teclado do Windows diretamente do painel do celular:
  - `Win + D` (Mostrar Área de Trabalho)
  - `Alt + Tab`
  - `Ctrl + Shift + Esc` (Gerenciador de Tarefas)
  - Combinações customizadas e teclas de função (`F1`–`F12`).

### 4. 🎵 Central de Mídia & Áudio Gigante
- **Grandes Botões Táteis:** Play/Pause, Próxima Faixa, Faixa Anterior.
- **Controle de Volume Master:** Botões grandes para `VOL +`, `VOL -` e `MUDO`.
- **Abertura Rápida:** Botão dedicado para abrir o Spotify no computador com 1 toque.

### 5. 💻 Telemetria Útil do Computador (Sem Poluição de IP)
- **Métricas Reais:** Monitoramento de CPU (%), Memória RAM em GB, Espaço Livre no SSD (`C:`) e Uptime real do Windows.
- **Perfis Alternáveis:**
  - **Modo Corporativo / Workstation (Padrão):** Métricas limpas de trabalho, sem poluição de dados gamers desnecessários.
  - **Modo Gamer (Opcional):** Ativa telemetria de GPU, FPS e temperatura de placa de vídeo.

### 6. ⚙️ Painel de Configurações no Navegador do Computador
- Ao tocar na engrenagem no celular, ele **abre automaticamente o painel de configurações na tela do computador** (`http://localhost:8770/settings`).
- Configure seus botões, atalhos, cidade do clima e modo de telemetria confortavelmente com mouse e teclado.

### 7. ☀️ Tela Sempre Ligada (Screen Wake Lock Nativo)
- Integração nativa com a **HTML5 Screen Wake Lock API**, garantindo que a tela do aparelho nunca apague ou diminua a iluminação durante o expediente, mesmo se desconectado da tomada.

---

## 🚀 Como Iniciar

### 1. Iniciar o Agente no Computador
O agente é **100% autocontido** e utiliza apenas a biblioteca padrão do Python 3 (sem necessidade de `pip install`):

```bash
python phonedesk_agent.py
```

O servidor iniciará automaticamente na porta `8770` e exibirá o IP da máquina na rede local:
```
=== DeskDock PC Agent v1.0.0 ===
Enderecos disponiveis na rede local:
 -> http://192.168.2.144:8770
[DeskDock] Servidor ativo em 0.0.0.0:8770
```

### 2. Conectar o Smartphone
1. Certifique-se de que o celular está na mesma rede Wi-Fi do computador.
2. No navegador do smartphone (Chrome / Edge / Kiosk), acesse o endereço IP exibido pelo agente (ex: `http://192.168.2.144:8770/`).
3. Deslize horizontalmente para alternar entre as telas:
   - **Tela 1:** StandBy iOS
   - **Tela 2:** Stream Deck de Aplicativos
   - **Tela 3:** Central de Mídia & Áudio
   - **Tela 4:** Telemetria de Hardware

---

## 🛠️ Configuração dos Atalhos (`config.json`)

Você pode personalizar os 12 botões através do painel web em `http://localhost:8770/settings` ou editando o arquivo `config.json`:

```json
{
  "id": "sankhya",
  "label": "Sankhya ERP",
  "action": "launch",
  "target": "sankhya"
}
```

Para atalhos de teclado:
```json
{
  "id": "desktop",
  "label": "Área de Trabalho",
  "action": "hotkey",
  "command": "win+d"
}
```

---

## 📄 Licença
Distribuído sob a licença MIT. Desenvolvido para máxima produtividade na mesa de trabalho.
