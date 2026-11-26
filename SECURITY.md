# Política de Segurança — Nuvra Dock

## Versões Suportadas

| Versão | Suportada |
| :--- | :---: |
| `1.0.x` | ✅ |

## Modelo de Segurança Local

O **Nuvra Dock** opera de forma 100% local (sem nuvem externa ou telemetria de terceiros):
- **Estúdio de Configuração (`127.0.0.1:8765`):** restrito exclusivamente à máquina local (loopback), protegido contra DNS rebinding e requisições cross-site (`Sec-Fetch-Site` + `Origin`).
- **Conexão via ADB (USB e ADB Wi-Fi):** tráfego encapsulado localmente via `adb reverse`.
- **Acesso Web na Rede Local (`0.0.0.0:8770`):** exige autenticação por token (`webRequireToken: true`) para dispositivos externos na LAN, bloqueando execução não autorizada de atalhos ou comandos.
- **Aplicativo Android (`com.nuvradock.app`):** WebView endurecido com `allowFileAccess(false)`, `allowContentAccess(false)` e lista branca estrita de origens locais.

## Como Reportar Vulnerabilidades

Não abra issues públicas para falhas de segurança sensíveis. Utilize a aba **Security › Report a vulnerability** (Private Vulnerability Reporting) do repositório no GitHub com passos de reprodução e impacto estimado.
