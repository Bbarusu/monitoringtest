KInfo
O KInfo é um painel de monitoramento leve e discreto que vive na bandeja do Windows.Ele exibe dados em tempo real sobre temperatura, carga, memória e energia do seu PC, sem comprometer o desempenho do sistema. O painel é totalmente flutuante e pode ser arrastado para qualquer área de trabalho ou monitor secundário.   

Kisland: A Ilha Dinâmica do Windows
Inspirada na interface dos smartphones modernos, a Kisland é uma pílula interativa e inteligente localizada no topo da tela.   Controle Multimídia e Áudio: Fechada, exibe a temperatura, volume e a música atual. Aberta, transforma-se em um player completo com suporte a capas de álbuns e atalhos de energia.   Monitoramento em Jogos: O modo jogo exibe o FPS real diretamente pelo rastreamento de eventos do Windows, sem injetar código que possa causar banimentos.   Proteção para Telas OLED: O painel se move sutilmente a cada 3 minutos, reduz a opacidade quando o mouse está distante e desaparece após 10 minutos de inatividade.   Integração com Inteligência Artificial: O anel pulsante da ilha pode ser configurado como um atalho rápido para o seu chat de IA favorito (como ChatGPT, Claude, Gemini ou Copilot).   


Kdrop: Compartilhamento Local Sem Complicação
O Kdrop transforma seu PC em um servidor local seguro para transferência de arquivos e textos.   Transferência via Navegador: O aplicativo hospeda uma página leve que você acessa pelo celular, permitindo baixar arquivos do PC ou enviar fotos e documentos diretamente para a bandeja do Windows.   Recuperação de Envios: Arquivos são transferidos em blocos de 8 MB. Se a conexão Wi-Fi cair, o sistema retoma o envio exatamente de onde parou.   Totalmente Local: Funciona via rede local (porta HTTP 48620) e MDNS, sem uploads para a nuvem e com descoberta automática entre computadores da mesma casa.   

Hardware e Desempenho
O sistema lê os componentes a fundo de maneira adaptativa, consumindo em média apenas 0,05% da CPU e 88 MB de RAM no hardware de referência.   
Uso de GPU por Programa: Identifica o consumo da placa de vídeo individualmente por processo, utilizando os contadores nativos do Windows.   
Leitura Avançada de Sensores: O acesso seguro aos sensores da placa-mãe e processador é feito pelo driver PawnIO. O instalador configura uma tarefa agendada inteligente para elevar os privilégios na inicialização sem importunar o usuário com alertas contínuos do sistema.   
Limites e Especificações Reais: As barras de carga baseiam-se em tetos reais de funcionamento (como TBP e TjMax) vindos de um catálogo de hardware embutido e atualizável, ou de testes de calibração térmica e de ventoinhas feitos no próprio PC.   
Teste de Estresse Integrado: Permite aplicar carga controlada na CPU para analisar reduções térmicas e monitorar o comportamento do clock sob pressão.   

Instalação e Requisitos 

Sistema: Windows 10 ou 11 (64 bits).   
Dependências: Requer o .NET 8 SDK.   
Setup Inteligente: O executável resolve sozinho as permissões do Windows Defender, configura regras de firewall para o Kdrop (apenas em redes privadas) e gerencia as atualizações de forma transparente, mantendo suas preferências.

## Estrutura

```
src/MonitorPC/
  Models/Snapshot.cs        uma leitura completa dos sensores
  Services/SensorService.cs leitura adaptativa e cálculo de energia
  Services/AppConfig.cs     configuração em JSON
  Views/FlyoutWindow.xaml   o painel e suas abas
  Views/BarraDeNucleo.cs    uma linha da grade de núcleos
  Views/BarraDeMetrica.cs   barra rotulada com valor (VRAM, potência, ventoinha)
  Views/LinhaDeDisco.cs     uma linha da lista de processos ou unidades
  Views/LinhaDeLegenda.cs   uma linha da legenda de energia
  Services/DiskProcessService.cs  uso de disco por processo
  Views/PersonalizacaoWindow.xaml  cor e transparência
  Views/ConfiguracaoWindow.xaml    limites de temperatura
  Services/PowerService.cs  perfis de energia
  Services/AlertService.cs  quando a temperatura passa do limite
  Services/ThemeService.cs  paleta clara e escura
  Services/OpenFilesService.cs  arquivos abertos por um processo
  Views/ArquivosWindow.xaml     a lista de arquivos abertos
  Views/MemoriaWindow.xaml  programas por consumo de memória
  Views/ComponentesWindow.xaml  quantidades e watts por unidade
  Views/LinhaDeAjuste.cs    uma linha de ajuste (contador ou número)
  Services/PerformanceService.cs  ações do Modo Desempenho
  Services/StressService.cs       carga de CPU controlada
  Views/SensoresWindow.xaml       lista de todos os sensores
  Services/ConsumoService.cs      registro de consumo por dia, em txt
  Services/GpuProcessService.cs   uso da placa de vídeo por programa
  Views/GpuProcessosWindow.xaml   a tela de GPU por programa
  Views/LinhaDeProcessoGpu.cs     uma linha dessa tela, com o botão de fechar
  Models/Teto.cs                  um valor máximo e a procedência dele
  Models/CurvaDeVentoinha.cs      curva rotação x PWM aprendida
  Services/CatalogoService.cs     ficha técnica por modelo, embutida e atualizável
  Services/EspecificacaoService.cs escolhe o teto de cada barra
  Services/CalibracaoService.cs   mede a rotação máxima real das ventoinhas
  Views/CalibracaoWindow.xaml     a tela da calibração
  Dados/catalogo.json             o catálogo embutido
  Views/EstresseWindow.xaml       a tela do teste de estresse
  Services/DiskLogService.cs      registro de picos de disco
  Views/LogDiscoWindow.xaml       a tela do log
  Views/DesempenhoWindow.xaml     a tela do Modo Desempenho
  Views/IlhaWindow.xaml           a ilha do topo da tela
  Services/MidiaService.cs        o que está tocando, pelo controle de mídia do Windows
  Services/VolumeService.cs       volume geral pelo Core Audio
  Services/TelaCheia.cs           se há programa em tela cheia agora
  App.xaml.cs               ícone da bandeja e ciclo de vida
```



## Hardware de referência

Ryzen 7 5700, ASUS Prime B550M-A, Radeon RX 7600, 24 GB em 3 módulos, 3 SSDs.

## Conflitos conhecidos

HWMonitor, Armoury Crate e Ryzen Master disputam o mesmo acesso de baixo nível aos
sensores. Rodando junto com eles, algumas leituras podem vir zeradas.
