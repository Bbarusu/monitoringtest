# Monitor de PC

Painel de monitoramento que vive na bandeja do Windows. Abre no canto inferior direito,
ancorado ao ícone da barra de tarefas, e mostra temperatura, carga, memória e energia
do PC sem pesar no sistema.

**Estado atual:** as cinco abas funcionais (Geral, CPU, GPU, Disco e Energia), com leitura real dos sensores,
mais a tela de Personalização e os perfis de energia.

## Requisitos

- Windows 10 ou 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Como rodar

```
cd src/MonitorPC
dotnet run
```

Para ler temperatura e potência da CPU e os sensores da placa-mãe, rode como
administrador — essas leituras dependem de um driver de acesso em modo kernel.
GPU, memória e discos funcionam sem elevação. Quando o driver não carrega, o painel
mostra um aviso em vez de exibir zeros.

Num passo futuro o instalador registra esse driver uma vez, e o app passa a abrir
sem exigir administrador.

## Configuração

Gerada na primeira execução em `%APPDATA%\MonitorPC\config.json`.

As **quantidades** de componentes (módulos de RAM, discos, ventoinhas) são detectadas
do hardware. Os campos terminados em `Manual` vêm vazios e servem para o que o PC não
enxerga — ventoinhas ligadas a uma controladora externa, por exemplo. Preenchidos, eles
passam a valer sobre a detecção.

Os **watts por unidade**, a capacidade e a eficiência da fonte, e os limites de
temperatura também ficam nesse arquivo.

Tudo isso é editável pela tela **Configurar componentes**, na aba Energia: cada quantidade
mostra se veio da detecção ou da mão, e há um botão para voltar à detecção. As mudanças
valem na hora, sem reabrir o app.

A aba Disco mede o uso por processo pelos contadores do próprio Windows, comparando
duas amostras. Essa medição só roda enquanto a aba está aberta: varrer a lista de
processos a cada segundo o tempo todo contrariaria a meta de não pesar no sistema.
Processos protegidos do sistema não permitem a leitura e ficam de fora.

Passado o limite, um aviso aparece no topo do painel, em qualquer aba, com os botões
"Ativar Econômico" e "Silenciar 10 min". O silêncio vale só para aquele componente:
silenciar o disco não esconde um aviso da CPU. Com a notificação ligada, o Windows também
avisa — uma vez por episódio, não a cada leitura — e clicar nela abre o painel. O aviso
só some quando a temperatura cai alguns graus abaixo do limite, para não piscar em volta
dele.

Na primeira execução o painel abre na configuração inicial, para os limites de
temperatura não ficarem no palpite do padrão. Depois, a mesma tela fica acessível
pelo ícone de ajustes no rodapé ou pelo menu do ícone da bandeja.

O botão **Ver log detalhado**, na aba Disco, abre o log: registra quem passou do
limite de MB/s que você definir, guarda os últimos 15 minutos e exporta em `.txt` com
nome e caminho de cada processo. O registro corre enquanto a tela está aberta.

Clicar em um processo — no log, na aba Disco ou na lista de memória — mostra **quais
arquivos ele tem abertos agora**. É a via leve: em vez de acompanhar cada leitura e
escrita pelo rastreamento de eventos do Windows, que gera milhares de eventos por segundo
e deixa uma sessão ativa no sistema, a consulta olha a tabela de descritores sob demanda.
Não dá a vazão por arquivo, mas responde qual arquivo o processo está mexendo. Processos
protegidos não permitem a inspeção.

Cada evento traz também a unidade que estava **mais ativa naquele momento**, e o seletor
filtra por ela. É uma pista, não uma atribuição exata: os contadores por processo do
Windows informam quantos bytes cada programa leu e escreveu, mas não em qual disco. Saber
isso com precisão exige o rastreamento por arquivo, que ainda não está implementado.

O card de **RAM**, na aba Geral, abre a lista de programas por consumo de memória, de onde
dá para fechá-los. Processos do Windows e de segurança aparecem marcados e sem o botão.

O botão **Desempenho** abre uma tela com o que será feito, em vez de agir sozinho:
trocar o plano de energia, limpar arquivos temporários e fechar programas escolhidos
na lista. Cada programa recebe o mesmo pedido de fechar que o botão X da janela, então
ainda dá para salvar o que estiver aberto; nada é forçado. Antivírus, drivers e
processos do Windows nunca entram na lista.

Os perfis Econômico e Equilibrado trocam o plano do Windows pelo `powercfg`. Quando o plano de
alto desempenho vem oculto na instalação, o app o duplica antes de ativar. Se a troca
falhar, o painel volta a marcar o plano que está realmente em vigor.

`FecharAoPerderFoco` vem desligado: o painel continua aberto quando você clica em
outra janela, o que permite comparar com outro programa ou tirar print. Quem fecha
é o botão de minimizar ou o ícone da bandeja. Ligue se preferir que ele suma sozinho.

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
  Services/DiskLogService.cs      registro de picos de disco
  Views/LogDiscoWindow.xaml       a tela do log
  Views/DesempenhoWindow.xaml     a tela do Modo Desempenho
  App.xaml.cs               ícone da bandeja e ciclo de vida
```

## Como funciona a leitura

| Situação | Intervalo |
|---|---|
| Painel aberto | 1 s |
| Painel fechado | 3 s |
| Vida útil e espaço livre | 30 s |

Medido no hardware de referência: média de 0,05 a 0,07% de CPU, com a memória
estabilizando em torno de 88 MB.

## Hardware de referência

Ryzen 7 5700, ASUS Prime B550M-A, Radeon RX 7600, 24 GB em 3 módulos, 3 SSDs.

## Conflitos conhecidos

HWMonitor, Armoury Crate e Ryzen Master disputam o mesmo acesso de baixo nível aos
sensores. Rodando junto com eles, algumas leituras podem vir zeradas.
