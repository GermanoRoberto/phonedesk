/**
 * DeskDock - Frontend Application Engine (InspoAI Studio Standard)
 * Funciona 100% autônomo no celular (Galaxy A04e) e sincroniza com o PC Agent
 */

// Estado da Aplicação
const state = {
  activeTab: 'deck',
  agentIp: localStorage.getItem('deskdock_agent_ip') || window.location.hostname || '127.0.0.1',
  agentPort: 8770,
  isOnline: false,
  wakeLock: null,
  theme: localStorage.getItem('deskdock_theme') || 'theme-dark',
  notepadText: localStorage.getItem('deskdock_notepad') || '',
  buttons: [],
  editingButtonIndex: null
};

// Ícones SVG Lucide embutidos (zero dependência de rede)
const ICONS = {
  rocket: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z"/><path d="m12 15-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z"/><path d="M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0"/><path d="M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5"/></svg>`,
  building: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="4" y="2" width="16" height="20" rx="2" ry="2"/><path d="M9 22v-4h6v4"/><path d="M8 6h.01"/><path d="M16 6h.01"/><path d="M8 10h.01"/><path d="M16 10h.01"/><path d="M8 14h.01"/><path d="M16 14h.01"/></svg>`,
  "trending-up": `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="23 6 13.5 15.5 8.5 10.5 1 18"/><polyline points="17 6 23 6 23 12"/></svg>`,
  monitor: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="3" width="20" height="14" rx="2" ry="2"/><line x1="8" y1="21" x2="16" y2="21"/><line x1="12" y1="17" x2="12" y2="21"/></svg>`,
  shield: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>`,
  mail: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z"/><polyline points="22,6 12,13 2,6"/></svg>`,
  "message-circle": `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z"/></svg>`,
  smartphone: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="5" y="2" width="14" height="20" rx="2" ry="2"/><line x1="12" y1="18" x2="12.01" y2="18"/></svg>`,
  headphones: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 18v-6a9 9 0 0 1 18 0v6"/><path d="M21 19a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3zM3 19a2 2 0 0 0 2 2h1a2 2 0 0 0 2-2v-3a2 2 0 0 0-2-2H3z"/></svg>`,
  folder: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>`,
  globe: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>`,
  lock: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>`,
  terminal: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg>`,
  code: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>`,
  cpu: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="4" y="4" width="16" height="16" rx="2"/><rect x="9" y="9" width="6" height="6"/><line x1="9" y1="1" x2="9" y2="4"/><line x1="15" y1="1" x2="15" y2="4"/><line x1="9" y1="20" x2="9" y2="23"/><line x1="15" y1="20" x2="15" y2="23"/><line x1="20" y1="9" x2="23" y2="9"/><line x1="20" y1="14" x2="23" y2="14"/><line x1="1" y1="9" x2="4" y2="9"/><line x1="1" y1="14" x2="4" y2="14"/></svg>`
};

// Configuração Padrão Genérica (personalize pelo seletor + Programas instalados em /settings)
const DEFAULT_BUTTONS = [
  { id: 'explorer', label: 'Arquivos', icon: 'folder', color: '#eab308', action: 'launch', target: 'explorer.exe' },
  { id: 'chrome', label: 'Navegador', icon: 'globe', color: '#f97316', action: 'launch', target: 'chrome.exe' },
  { id: 'terminal', label: 'Terminal', icon: 'terminal', color: '#475569', action: 'launch', target: 'wt.exe' },
  { id: 'lock_pc', label: 'Bloquear PC', icon: 'lock', color: '#ef4444', action: 'lock', target: '' }
];

// Inicialização
document.addEventListener('DOMContentLoaded', () => {
  initTheme();
  initButtons();
  initNavigation();
  initClock();
  initWeather();
  initTelemetryLoop();
  initNotepad();
  initWakeLock();
  initFullscreen();
  initMediaControls();
  initFeed();
  initModal();

  // Registrar Service Worker
  if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('sw.js').catch(() => {});
  }
});

// Toast de Feedback
function showToast(msg) {
  const toast = document.getElementById('toast');
  toast.textContent = msg;
  toast.classList.add('show');
  setTimeout(() => toast.classList.remove('show'), 2500);
}

// Vibração tátil
function haptic() {
  if ('vibrate' in navigator) {
    try { navigator.vibrate(25); } catch (e) {}
  }
}

// 1. TEMA
function initTheme() {
  document.body.className = state.theme;
  document.querySelectorAll('.theme-choice').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.theme === state.theme);
    btn.addEventListener('click', () => {
      state.theme = btn.dataset.theme;
      document.body.className = state.theme;
      localStorage.setItem('deskdock_theme', state.theme);
      document.querySelectorAll('.theme-choice').forEach(b => b.classList.toggle('active', b === btn));
    });
  });
}

// 2. BOTÕES & DECK
function initButtons() {
  const saved = localStorage.getItem('deskdock_buttons');
  if (saved) {
    try { state.buttons = JSON.parse(saved); } catch (e) { state.buttons = DEFAULT_BUTTONS; }
  } else {
    state.buttons = DEFAULT_BUTTONS;
  }
  renderDeckGrid();
}

function renderDeckGrid() {
  const grid = document.getElementById('deckGrid');
  grid.innerHTML = '';

  state.buttons.forEach((btn, index) => {
    const el = document.createElement('div');
    el.className = 'deck-btn';
    el.style.setProperty('--btn-color', btn.color || '#38bdf8');
    
    const svgIcon = ICONS[btn.icon] || ICONS.rocket;

    el.innerHTML = `
      <div class="deck-btn-edit-badge" title="Editar botão">✎</div>
      <div class="btn-icon-wrapper" style="color: ${btn.color || '#38bdf8'}">
        ${svgIcon}
      </div>
      <span class="btn-label">${btn.label}</span>
    `;

    // Toque / Clique para disparar ação
    el.addEventListener('click', (e) => {
      // Se clicou no badge de edição
      if (e.target.classList.contains('deck-btn-edit-badge')) {
        openEditModal(index);
        return;
      }
      triggerButtonAction(btn);
    });

    // Pressionar e segurar para editar (mobile)
    let pressTimer;
    el.addEventListener('touchstart', () => {
      pressTimer = setTimeout(() => {
        haptic();
        openEditModal(index);
      }, 700);
    });
    el.addEventListener('touchend', () => clearTimeout(pressTimer));
    el.addEventListener('touchmove', () => clearTimeout(pressTimer));

    grid.appendChild(el);
  });
}

async function triggerButtonAction(btn) {
  haptic();
  if (!state.isOnline) {
    showToast(`PC Desconectado • Tentando reconectar...`);
  }

  try {
    const res = await fetch(`http://${state.agentIp}:${state.agentPort}/api/action`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        action: btn.action,
        payload: {
          target: btn.target,
          command: btn.command
        }
      })
    });
    if (res.ok) {
      showToast(`Executado: ${btn.label}`);
    } else {
      showToast(`Falha ao executar ${btn.label}`);
    }
  } catch (err) {
    showToast(`Erro de conexão com o PC (${state.agentIp})`);
  }
}

// 3. NAVEGAÇÃO ENTRE ABAS
function initNavigation() {
  document.querySelectorAll('.nav-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      haptic();
      const tab = btn.dataset.tab;
      state.activeTab = tab;
      
      document.querySelectorAll('.nav-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');

      document.querySelectorAll('.tab-pane').forEach(p => p.classList.remove('active'));
      const pane = document.getElementById(`pane-${tab}`);
      if (pane) pane.classList.add('active');
    });
  });
}

// 4. RELÓGIO EXECUTIVO (STANDBY)
function initClock() {
  const elHours = document.getElementById('clockHours');
  const elMinutes = document.getElementById('clockMinutes');
  const elSeconds = document.getElementById('clockSeconds');
  const elDate = document.getElementById('clockFullDate');

  const dias = ['Domingo', 'Segunda-feira', 'Terça-feira', 'Quarta-feira', 'Quinta-feira', 'Sexta-feira', 'Sábado'];
  const meses = ['Janeiro', 'Fevereiro', 'Março', 'Abril', 'Maio', 'Junho', 'Julho', 'Agosto', 'Setembro', 'Outubro', 'Novembro', 'Dezembro'];

  function update() {
    const now = new Date();
    elHours.textContent = String(now.getHours()).padStart(2, '0');
    elMinutes.textContent = String(now.getMinutes()).padStart(2, '0');
    elSeconds.textContent = String(now.getSeconds()).padStart(2, '0');

    const diaSemana = dias[now.getDay()];
    const diaMes = now.getDate();
    const mes = meses[now.getMonth()];
    const ano = now.getFullYear();

    elDate.textContent = `${diaSemana}, ${diaMes} de ${mes} de ${ano}`;
  }

  update();
  setInterval(update, 1000);
}

// 5. CLIMA AUTÔNOMO (Open-Meteo DWD ICON)
async function initWeather() {
  const elTemp = document.getElementById('weatherTemp');
  const elCondition = document.getElementById('weatherCondition');
  const elFeels = document.getElementById('weatherFeels');
  const elIcon = document.getElementById('weatherIconLarge');
  const elHum = document.getElementById('weatherHumidity');
  const elWind = document.getElementById('weatherWind');
  const elRain = document.getElementById('weatherRain');

  // Códigos WMO da OMM para Português
  function parseWeatherCode(code, isDay) {
    switch (code) {
      case 0: return { desc: isDay ? 'Ensolarado / Céu Limpo' : 'Noite Limpa', icon: isDay ? '☀️' : '🌙' };
      case 1:
      case 2: return { desc: 'Parcialmente Nublado', icon: isDay ? '🌤️' : '☁️' };
      case 3: return { desc: 'Encoberto', icon: '☁️' };
      case 45:
      case 48: return { desc: 'Nevoeiro / Névoa', icon: '🌫️' };
      case 51:
      case 53:
      case 55: return { desc: 'Garoa Leve', icon: '🌦️' };
      case 61:
      case 63: return { desc: 'Chuva Moderada', icon: '🌧️' };
      case 65: return { desc: 'Chuva Forte', icon: '🌧️' };
      case 80:
      case 81:
      case 82: return { desc: 'Pancadas de Chuva', icon: '🌦️' };
      case 95:
      case 96:
      case 99: return { desc: 'Tempestade com Raios', icon: '⛈️' };
      default: return { desc: 'Tempo Firme', icon: '⛅' };
    }
  }

  async function fetchWeather() {
    try {
      // Coordenadas precisas de Juiz de Fora (-21.7642, -43.3496) com modelo DWD ICON Seamless
      const url = `https://api.open-meteo.com/v1/forecast?latitude=-21.7642&longitude=-43.3496&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,wind_speed_10m&models=icon_seamless&timezone=America%2FSao_Paulo`;
      const res = await fetch(url);
      const data = await res.json();
      const cur = data.current;

      const info = parseWeatherCode(cur.weather_code, cur.is_day);
      elTemp.textContent = Math.round(cur.temperature_2m);
      elCondition.textContent = info.desc;
      elFeels.textContent = `${Math.round(cur.apparent_temperature)}°C`;
      elIcon.textContent = info.icon;
      elHum.textContent = `${cur.relative_humidity_2m}%`;
      elWind.textContent = `${Math.round(cur.wind_speed_10m)} km/h`;
      elRain.textContent = `${cur.precipitation} mm`;

      // Salva no cache local
      localStorage.setItem('deskdock_weather_cache', JSON.stringify({ cur, info, timestamp: Date.now() }));
    } catch (e) {
      // Usar cache se estiver offline
      const cached = localStorage.getItem('deskdock_weather_cache');
      if (cached) {
        try {
          const { cur, info } = JSON.parse(cached);
          elTemp.textContent = Math.round(cur.temperature_2m);
          elCondition.textContent = `${info.desc} (cache)`;
          elFeels.textContent = `${Math.round(cur.apparent_temperature)}°C`;
          elIcon.textContent = info.icon;
          elHum.textContent = `${cur.relative_humidity_2m}%`;
          elWind.textContent = `${Math.round(cur.wind_speed_10m)} km/h`;
          elRain.textContent = `${cur.precipitation} mm`;
        } catch (err) {}
      }
    }
  }

  fetchWeather();
  setInterval(fetchWeather, 15 * 60 * 1000); // A cada 15 min
}

// 6. TELEMETRIA DO PC & STATUS LOOP
function initTelemetryLoop() {
  const statusPill = document.getElementById('statusPill');
  const statusDot = statusPill.querySelector('.status-dot');
  const statusText = document.getElementById('statusText');
  const cpuVal = document.getElementById('cpuVal');
  const cpuBar = document.getElementById('cpuBar');
  const ramVal = document.getElementById('ramVal');
  const ramBar = document.getElementById('ramBar');

  async function pollStatus() {
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 2000);
      const res = await fetch(`http://${state.agentIp}:${state.agentPort}/api/status`, {
        signal: controller.signal
      });
      clearTimeout(timeoutId);

      if (res.ok) {
        const data = await res.json();
        state.isOnline = true;
        statusDot.className = 'status-dot dot-online';
        statusText.textContent = data.hostname || 'Online';
        
        const cpu = data.cpu_percent || 0;
        cpuVal.textContent = `${cpu}%`;
        cpuBar.style.width = `${cpu}%`;
        
        const mem = data.memory ? data.memory.load_percent : 0;
        ramVal.textContent = `${mem}%`;
        ramBar.style.width = `${mem}%`;
        return;
      }
    } catch (e) {}

    // Se falhar
    state.isOnline = false;
    statusDot.className = 'status-dot dot-offline';
    statusText.textContent = 'Aguardando PC';
    cpuVal.textContent = '--%';
    cpuBar.style.width = '0%';
    ramVal.textContent = '--%';
    ramBar.style.width = '0%';
  }

  pollStatus();
  setInterval(pollStatus, 2500);
}

// 7. WAKE LOCK API (Manter Tela do Celular Sempre Acesa)
async function initWakeLock() {
  const btn = document.getElementById('btnWakeLock');
  
  async function requestLock() {
    if ('wakeLock' in navigator) {
      try {
        state.wakeLock = await navigator.wakeLock.request('screen');
        btn.classList.add('active');
        state.wakeLock.addEventListener('release', () => {
          btn.classList.remove('active');
        });
      } catch (err) {}
    }
  }

  btn.addEventListener('click', async () => {
    haptic();
    if (state.wakeLock) {
      await state.wakeLock.release();
      state.wakeLock = null;
      btn.classList.remove('active');
      showToast('Wake Lock desativado (tela pode apagar)');
    } else {
      await requestLock();
      showToast('Wake Lock ATIVO (tela nunca apaga)');
    }
  });

  // Re-solicitar se o app voltar para o primeiro plano
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && !state.wakeLock) {
      requestLock();
    }
  });

  // Tenta ativar de imediato
  requestLock();
}

// 8. TELA CHEIA & KIOSK AUTOMÁTICO
function initFullscreen() {
  const btn = document.getElementById('btnFullscreen');
  
  function enterFullscreen() {
    if (!document.fullscreenElement) {
      const el = document.documentElement;
      if (el.requestFullscreen) {
        el.requestFullscreen().catch(() => {});
      } else if (el.webkitRequestFullscreen) {
        el.webkitRequestFullscreen();
      }
    }
  }

  btn.addEventListener('click', () => {
    haptic();
    if (!document.fullscreenElement) {
      enterFullscreen();
    } else {
      if (document.exitFullscreen) document.exitFullscreen().catch(() => {});
    }
  });

  // Kiosk Automático: no primeiro toque na tela, oculta barras do navegador e entra em tela cheia total
  window.addEventListener('touchstart', () => {
    enterFullscreen();
  }, { once: true });
  
  window.addEventListener('click', () => {
    enterFullscreen();
  }, { once: true });
}

// 9. CONTROLES MULTIMÍDIA DO TOPO
function initMediaControls() {
  async function sendMedia(cmd) {
    haptic();
    try {
      await fetch(`http://${state.agentIp}:${state.agentPort}/api/action`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action: 'media', payload: { command: cmd } })
      });
    } catch (e) {
      showToast('Erro ao enviar controle multimídia');
    }
  }

  document.getElementById('btnVolDown').addEventListener('click', () => sendMedia('voldown'));
  document.getElementById('btnMute').addEventListener('click', () => sendMedia('mute'));
  document.getElementById('btnVolUp').addEventListener('click', () => sendMedia('volup'));
  document.getElementById('btnPlayPause').addEventListener('click', () => sendMedia('playpause'));
}

// 10. BLOCO DE NOTAS & FEED
function initNotepad() {
  const notepad = document.getElementById('deskNotepad');
  notepad.value = state.notepadText;

  notepad.addEventListener('input', () => {
    state.notepadText = notepad.value;
    localStorage.setItem('deskdock_notepad', state.notepadText);
  });

  document.getElementById('btnClearNotes').addEventListener('click', () => {
    notepad.value = '';
    state.notepadText = '';
    localStorage.setItem('deskdock_notepad', '');
    showToast('Anotações limpas');
  });

  document.getElementById('btnCopyNotes').addEventListener('click', () => {
    navigator.clipboard.writeText(notepad.value).then(() => {
      showToast('Copiado para a área de transferência');
    }).catch(() => {
      showToast('Texto copiado');
    });
  });
}

function initFeed() {
  const list = document.getElementById('newsList');
  const btnRefresh = document.getElementById('btnRefreshFeed');

  async function loadFeed() {
    list.innerHTML = '<div class="news-placeholder">Carregando feed de segurança...</div>';
    try {
      // Usar feed RSS público via conversor JSON CORS
      const rssUrl = encodeURIComponent('https://www.cybersecbrazil.com.br/blog-feed.xml');
      const res = await fetch(`https://api.rss2json.com/v1/api.json?rss_url=${rssUrl}`);
      const data = await res.json();
      
      if (data.status === 'ok' && data.items && data.items.length > 0) {
        list.innerHTML = '';
        data.items.slice(0, 10).forEach(item => {
          const card = document.createElement('div');
          card.className = 'news-item';
          card.innerHTML = `
            <div class="news-title">${item.title}</div>
            <div class="news-meta">${new Date(item.pubDate).toLocaleDateString('pt-BR')} • Toque para ler</div>
          `;
          card.addEventListener('click', () => {
            haptic();
            // Abre no PC se conectado, ou no próprio celular
            if (state.isOnline) {
              fetch(`http://${state.agentIp}:${state.agentPort}/api/action`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ action: 'launch', payload: { target: item.link } })
              });
              showToast('Abrindo notícia no PC...');
            } else {
              window.open(item.link, '_blank');
            }
          });
          list.appendChild(card);
        });
        return;
      }
    } catch (e) {}

    // Fallback com alertas práticos para a mesa de TI
    list.innerHTML = `
      <div class="news-item">
        <div class="news-title">Patch Tuesday & Atualizações Críticas</div>
        <div class="news-meta">Verifique a fila de pacotes WSUS e servidores locais</div>
      </div>
      <div class="news-item">
        <div class="news-title">Monitoramento de Túneis WireGuard & RDP</div>
        <div class="news-meta">Audite conexões simultâneas de trabalho remoto</div>
      </div>
      <div class="news-item">
        <div class="news-title">Exemplo de Feed RSS</div>
        <div class="news-meta">Rotina noturna de snapshot validada com sucesso</div>
      </div>
    `;
  }

  btnRefresh.addEventListener('click', () => {
    haptic();
    loadFeed();
  });

  loadFeed();
}

// 11. CONFIGURAÇÕES & MODAL DE EDIÇÃO
function initModal() {
  const modal = document.getElementById('editModal');
  const btnClose = document.getElementById('btnModalClose');
  const btnCancel = document.getElementById('btnModalCancel');
  const btnSave = document.getElementById('btnModalSave');

  const inLabel = document.getElementById('editBtnLabel');
  const inIcon = document.getElementById('editBtnIcon');
  const inAction = document.getElementById('editBtnAction');
  const inTarget = document.getElementById('editBtnTarget');
  const inCommand = document.getElementById('editBtnCommand');
  const groupTarget = document.getElementById('groupTarget');
  const groupCommand = document.getElementById('groupCommand');

  inAction.addEventListener('change', () => {
    if (inAction.value === 'powershell') {
      groupTarget.style.display = 'none';
      groupCommand.style.display = 'flex';
    } else {
      groupTarget.style.display = 'flex';
      groupCommand.style.display = 'none';
    }
  });

  window.openEditModal = function(index) {
    state.editingButtonIndex = index;
    const btn = state.buttons[index];
    inLabel.value = btn.label;
    inIcon.value = btn.icon || 'rocket';
    inAction.value = btn.action || 'launch';
    inTarget.value = btn.target || '';
    inCommand.value = btn.command || '';
    inAction.dispatchEvent(new Event('change'));
    modal.classList.add('open');
  };

  function closeModal() {
    modal.classList.remove('open');
    state.editingButtonIndex = null;
  }

  btnClose.addEventListener('click', closeModal);
  btnCancel.addEventListener('click', closeModal);

  btnSave.addEventListener('click', () => {
    if (state.editingButtonIndex !== null) {
      const idx = state.editingButtonIndex;
      state.buttons[idx].label = inLabel.value.trim() || 'Sem título';
      state.buttons[idx].icon = inIcon.value;
      state.buttons[idx].action = inAction.value;
      state.buttons[idx].target = inTarget.value.trim();
      state.buttons[idx].command = inCommand.value.trim();

      localStorage.setItem('deskdock_buttons', JSON.stringify(state.buttons));
      renderDeckGrid();
      closeModal();
      showToast('Botão atualizado com sucesso!');
    }
  });

  // Configuração de IP do Agente
  const cfgIpInput = document.getElementById('cfgAgentIp');
  cfgIpInput.value = state.agentIp;

  document.getElementById('btnSaveIp').addEventListener('click', () => {
    haptic();
    const newIp = cfgIpInput.value.trim();
    if (newIp) {
      state.agentIp = newIp;
      localStorage.setItem('deskdock_agent_ip', newIp);
      showToast(`IP alterado para ${newIp}`);
    }
  });

  // Scanner de Subrede Inteligente
  document.getElementById('btnScanSubnet').addEventListener('click', async () => {
    haptic();
    const scanStatus = document.getElementById('scanStatus');
    scanStatus.textContent = 'Escaneando subrede local em busca do DeskDock PC Agent...';
    
    // Obter os 3 primeiros octetos do IP atual
    const parts = state.agentIp.split('.');
    if (parts.length === 4) {
      const baseSubnet = `${parts[0]}.${parts[1]}.${parts[2]}`;
      scanStatus.textContent = `Varrendo ${baseSubnet}.1 até ${baseSubnet}.254...`;
      
      let found = false;
      // Escaneia em blocos rápidos paralelos
      const batchSize = 25;
      for (let i = 1; i <= 254; i += batchSize) {
        if (found) break;
        const promises = [];
        for (let j = i; j < i + batchSize && j <= 254; j++) {
          const testIp = `${baseSubnet}.${j}`;
          promises.push(
            fetch(`http://${testIp}:${state.agentPort}/api/ping`, { mode: 'cors' })
              .then(r => r.text())
              .then(text => {
                if (text === 'pong' && !found) {
                  found = true;
                  state.agentIp = testIp;
                  cfgIpInput.value = testIp;
                  localStorage.setItem('deskdock_agent_ip', testIp);
                  scanStatus.textContent = `Encontrado PC Agent em ${testIp}! Conectando...`;
                  showToast(`DeskDock Agent encontrado em ${testIp}!`);
                }
              })
              .catch(() => {})
          );
        }
        await Promise.all(promises);
      }
      if (!found) {
        scanStatus.textContent = `Nenhum outro agente respondendo na subrede ${baseSubnet}.0/24.`;
      }
    }
  });

  // Restaurar botões
  document.getElementById('btnResetButtons').addEventListener('click', () => {
    if (confirm('Deseja restaurar os 12 botões padrão de trabalho?')) {
      state.buttons = DEFAULT_BUTTONS;
      localStorage.removeItem('deskdock_buttons');
      renderDeckGrid();
      showToast('Botões restaurados para o padrão');
    }
  });

  // Exportar configuração
  document.getElementById('btnExportConfig').addEventListener('click', () => {
    const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(state.buttons, null, 2));
    const dlAnchor = document.createElement('a');
    dlAnchor.setAttribute("href", dataStr);
    dlAnchor.setAttribute("download", "deskdock_buttons_backup.json");
    dlAnchor.click();
  });
}
