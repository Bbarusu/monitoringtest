# Monitor de PC

Painel de monitoramento que vive na bandeja do Windows. Abre no canto inferior direito,
ancorado ao ícone da barra de tarefas, e mostra temperatura, carga, memória e energia
do PC sem pesar no sistema.

**Estado atual:** abas Geral e CPU funcionais, com leitura real dos sensores,
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

Os perfis de energia trocam o plano do Windows pelo `powercfg`. Quando o plano de
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
  Views/PersonalizacaoWindow.xaml  cor e transparência
  Services/PowerService.cs  perfis de energia
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
