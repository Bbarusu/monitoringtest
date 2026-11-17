<div align="center">

<img src="src/MonitorPC/kinfo.ico" width="96" alt="KInfo">

# KInfo

**Painel de monitoramento leve para Windows que vive na bandeja, com ilha dinâmica (Kisland) e compartilhamento local (Kdrop).**

[Kisland](#kisland-a-ilha-dinâmica-do-windows) ·
[Kdrop](#kdrop-compartilhamento-local-sem-complicação) ·
[Hardware e desempenho](#hardware-e-desempenho) ·
[Instalação](#instalação-e-requisitos) ·
[Estrutura](#estrutura)

</div>

O **KInfo** exibe em tempo real temperatura, carga, memória, disco e energia do PC sem pesar no sistema. O painel abre ancorado na bandeja do Windows, é totalmente flutuante e pode ser arrastado para qualquer canto da área de trabalho ou monitor secundário — além de redesenhar o próprio ícone da bandeja com a temperatura atual da CPU.

---

## Painel principal

Cinco abas fixas com visual adaptável (temas **Escuro**, **Claro**, **Sistema** e **Preto OLED**), cor de destaque personalizável e transparência ajustável:

| Aba | O que mostra |
|---|---|
| **Geral** | Temperatura da CPU no anel principal, uso de RAM, cards expansíveis de GPU/RAM e perfis de energia |
| **CPU** | Grade dinâmica por núcleo ou thread, clocks, tensão, potência e **Teste de estresse** integrado |
| **GPU** | Temperatura da borda, hot spot (com alerta de diferença alta), VRAM, clocks, ventoinha e uso por processo |
| **Disco** | Temperatura, leitura/escrita em MB/s, vida útil SMART, uso por processo, arquivos abertos e log de picos |
| **Energia** | Potência medida (CPU/GPU) + estimada, consumo acumulado em Wh/kWh, média/pico e calibração de componentes |

> **Atenção:** o menu da bandeja é desenhado pelo próprio app (não pelo menu nativo do Windows) para continuar aparecendo normalmente mesmo com jogos em tela cheia.

---

## Kisland: a ilha dinâmica do Windows

Inspirada na ilha interativa dos smartphones modernos, a **Kisland** fica sempre acessível sem roubar o foco da janela em uso (`WS_EX_NOACTIVATE`).

- **Posição livre e modo vertical:** arraste para qualquer lugar da tela ou entre monitores. Perto do centro do topo ela encaixa de volta no padrão; perto das bordas esquerda ou direita (ou pelo menu de clique direito / Personalização), assume formato vertical compacto e expande ancorada dentro da tela.
- **Multimídia, volume e mixer:** fechada, exibe música atual, temperaturas, RAM, disco, potência, relógio e volume. Aberta, traz player com capa, controles, seletor de saídas de áudio e mixer por aplicativo com controle de microfone.
- **Monitoramento em jogos:** exibe o FPS real por rastreamento de eventos do Windows (DXGI/D3D9, estilo PresentMon), sem injetar código no jogo e sem risco de banimento.
- **Proteção para telas OLED:** desloca 1–2 px a cada 3 minutos, escurece quando o cursor está longe e se esconde após 10 minutos sem entrada no PC (além da opção de fundo preto puro).
- **Atalhos e IA:** o anel pulsante pode abrir seu chat de IA favorito (ChatGPT, Claude, Gemini, Copilot, Perplexity, DeepSeek ou endereço próprio), e a área expandida comporta até 6 atalhos de apps ou sites.

---

## Kdrop: compartilhamento local sem complicação

O **Kdrop** transforma a bandeja da Kisland em um ponto de troca rápida de arquivos, pastas e textos na mesma rede Wi-Fi — sem nuvem, sem conta e sem servidor externo.

- **Página para o celular:** escaneando o QR Code na Kisland, você abre uma página leve no navegador do celular para baixar itens do PC ou enviar fotos, vídeos e documentos direto para a bandeja.
- **Envio retomável em partes:** arquivos grandes sobem em blocos de 8 MB; se o Wi-Fi oscilar, a página retoma exatamente de onde parou.
- **Pastas e textos:** pastas baixadas pelo celular saem compactadas em `.zip` sob demanda; textos e links copiados entram na bandeja com botões rápidos de copiar e abrir.
- **Segurança na rede:** todo envio passa por aceite na Kisland (prazo de 60 s), exceto aparelhos marcados como confiáveis, e a regra de firewall vale apenas para redes privadas e de domínio.

| Serviço | Porta / Protocolo | Função |
|---|---|---|
| Servidor Web | `48620` / TCP (HTTP) | Página do celular e transferência de arquivos |
| Descoberta local | `48621` / UDP | Anúncio da bandeja entre PCs com KInfo na rede |
| Endereço fixo | `5353` / UDP (mDNS) | Responde por `kdrop-<nome>.local` na rede doméstica |

---

## Hardware e desempenho

Medido no hardware de referência a cada 3 segundos: média de **0,05% a 0,07% de CPU** e memória estabilizada em torno de **88 MB**.

- **Leitura adaptativa:** intervalo de `1 s` com o painel aberto, `3 s` fechado e `30 s` para sensores lentos; listas caras (disco e GPU por processo) só rodam com a respectiva tela aberta.
- **GPU por programa:** identifica consumo e VRAM por processo via contadores nativos do Windows (`GPU Engine` e `GPU Process Memory`), independente do fabricante.
- **Driver seguro sem UAC repetitivo:** utiliza o driver assinado **PawnIO** via `LibreHardwareMonitorLib`. O instalador cria uma tarefa agendada com privilégio elevado uma única vez, evitando pedidos do Windows a cada inicialização.
- **Tetos reais e calibração:** as barras usam limites reais de fábrica (PPT, TBP, TjMax, boost) de um catálogo embutido e atualizável, além de aprendizado automático de curvas PWM e calibração de ventoinhas a 100%.
- **Teste de estresse integrado:** aplica carga controlada nas threads da CPU com parada automática perto do limite térmico, apontando queda de clock por redução térmica.

---

## Instalação e requisitos

### Para usar (instalador pronto)

- **Sistema:** Windows 10 ou 11 (64 bits).
- **Instalador:** baixe o `KInfo-Setup.exe` na página de [releases](https://github.com/Bbarusu/kinfo-releases/releases/latest).
- **Setup completo:** instala o app, configura a tarefa elevada dos sensores, oferece a instalação do **PawnIO**, cria regra de firewall do Kdrop (redes privadas), adiciona exceção opcional no Windows Defender e gerencia atualizações mantendo suas configurações.


---

## Estrutura

```text
src/
  Comum/
    Versao.cs                        leitura e comparação de versão compartilhada
  MonitorPC/
    App.xaml.cs                      ícone da bandeja, elevação, atualizações e ciclo de vida
    Dados/
      catalogo.json                  ficha técnica embutida de CPUs e GPUs
      kdrop.html                     página web servida ao celular pelo Kdrop
    Models/
      Snapshot.cs                    leitura completa dos sensores em um ciclo
      Teto.cs                        limite máximo de barra e procedência
      CurvaDeVentoinha.cs            curva rotação × PWM aprendida entre sessões
      ...                            modelos de disco, sensores e processos
    Services/
      SensorService.cs               leitura adaptativa e cálculo de energia
      EspecificacaoService.cs        escolha de tetos (manual, hardware, curva, catálogo)
      CatalogoService.cs             catálogo embutido com cache e atualização semanal
      CalibracaoService.cs           medição de rotação máxima real das ventoinhas
      ConsumoService.cs              registro diário de consumo em texto
      AlertService.cs                alertas térmicos com histerese e silenciamento
      PowerService.cs                alternância de planos de energia do Windows
      PerformanceService.cs          ações do Modo Desempenho e gestão de processos
      StressService.cs               carga controlada de CPU por threads
      DiskProcessService.cs          taxa de I/O de disco por processo
      DiskLogService.cs              histórico de picos de disco
      GpuProcessService.cs           uso de GPU e VRAM por processo via PDH
      OpenFilesService.cs            arquivos abertos por processo sob demanda
      FpsService.cs                  contador de FPS via ETW (DXGI / D3D9) e detector de jogos
      MidiaService.cs                controle de mídia via SMTC do Windows
      VolumeService.cs               volume, mudo e saídas/entradas via Core Audio
      MixerService.cs                mixer por aplicativo e microfone
      KdropService.cs                servidor HTTP, descoberta UDP e fila de transferências
      NomeNaRede.cs                  responder mDNS mínimo para kdrop-<nome>.local
      ThemeService.cs                temas Claro, Escuro, Sistema e Preto (OLED)
      AtualizacaoService.cs          verificação, download e validação SHA-256 de updates
      AppConfig.cs                   persistência em %APPDATA%\KInfo\config.json
    Views/
      FlyoutWindow.xaml              painel principal da bandeja (5 abas)
      IlhaWindow.xaml                Kisland flutuante (horizontal, vertical e expandida)
      PersonalizacaoWindow.xaml      temas, cores, transparência e ajustes da Kisland
      ConfiguracaoWindow.xaml        limites térmicos e preferências gerais
      GuiaKislandWindow.xaml         guia interativo de apresentação da Kisland
      NovidadesWindow.xaml           apresentação do Kdrop
      ...                            telas auxiliares (Sensores, Memória, GPU, Disco, Estresse)
  MonitorPC.Setup/
    Instalacao.cs                    instalação, tarefa agendada, PawnIO, atualização e remoção
    SetupWindow.xaml                 interface do instalador/atualizador
```

---

## Hardware de referência e conflitos

- **Hardware de referência:** Ryzen 7 5700, ASUS Prime B550M-A, Radeon RX 7600, 24 GB em 3 módulos e 3 SSDs.
- **Conflitos conhecidos:** **HWMonitor**, **Armoury Crate** e **Ryzen Master** disputam o mesmo acesso de baixo nível aos sensores. Rodando junto com eles, algumas leituras podem vir zeradas.

---

## Estado

Projeto pessoal em desenvolvimento contínuo (versão atual `1.0.3`). Feito para uso diário no Windows 10 e 11 sem depender de assinaturas ou serviços pagos de terceiros.
