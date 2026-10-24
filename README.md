# Monitor de PC

Painel de monitoramento que vive na bandeja do Windows. Abre no canto inferior direito,
ancorado ao ícone da barra de tarefas, e mostra temperatura, carga, memória e energia
do PC sem pesar no sistema. Dá para arrastá-lo para qualquer lugar da área de trabalho, inclusive para um segundo
monitor. A posição vale só enquanto o app estiver aberto: cada abertura começa no canto,
saindo da bandeja. "Voltar ao canto", no menu da bandeja, o traz de volta a qualquer
momento. O botão
de energia, no topo do painel, encerra o app por completo.

O menu do ícone da bandeja é desenhado pelo próprio app, e não pelo menu padrão do
Windows: aquele depende de o app virar primeiro plano para aparecer, e com um jogo em tela
cheia o Windows nega esse pedido, deixando o menu atrás da barra de tarefas.

O ícone de **todos os sensores**, no rodapé do painel, lista tudo que o hardware reporta,
agrupado por componente e com filtro por nome: as temperaturas e tensões da placa-mãe, a
potência por núcleo, as horas de cada disco — inclusive o que as abas não mostram. Os
nomes sem rótulo vêm assim do próprio chip da placa.

O ícone da bandeja mostra a temperatura da CPU: é desenhado a cada leitura, em um quadrado
com a cor de destaque, e some do contorno quando os sensores de baixo nível não estão
disponíveis.

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

A **potência máxima da GPU** também fica lá, mas vazia por padrão: o app guarda entre
sessões a maior potência que os sensores já mediram e usa esse valor como escala da barra,
que vai se aproximando do teto real conforme a GPU é exigida. Preencher o campo é só um
atalho para quem sabe o número da ficha técnica.

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
limite de MB/s que você definir, guarda os últimos 15 minutos. O registro corre enquanto a
tela está aberta.

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

Essa tela também reinicia o **Explorer**, útil quando a barra de tarefas trava ou a memória
dele cresce demais. O app é outro processo e não cai junto; o que some é a área de
notificação, recriada pelo Explorer, então o ícone da bandeja é reinstalado logo depois.
Janelas de pastas abertas são fechadas.

O botão **Desempenho** abre uma tela com o que será feito, em vez de agir sozinho:
trocar o plano de energia, limpar arquivos temporários e fechar programas escolhidos
na lista. Cada programa recebe o mesmo pedido de fechar que o botão X da janela, então
ainda dá para salvar o que estiver aberto; nada é forçado. Antivírus, drivers e
processos do Windows nunca entram na lista.

O botão **Teste de estresse**, na aba CPU, põe carga controlada no processador para ver
como ele se comporta sob pressão. Você escolhe quantas threads e por quanto tempo; o teste
para sozinho ao chegar perto do limite de temperatura configurado, ao fim do tempo, ou
quando você mandar. No fim ele relata temperatura e potência máximas e a faixa de clock —
e, se o clock caiu mais de 10% com a carga constante, aponta redução térmica, que é o
achado que justifica um teste desses viver dentro de um monitor.

Só a CPU é testada. GPU exigiria uma carga gráfica real, com dependência de renderização,
e um teste de RAM que apenas aloca blocos empurra o sistema para o arquivo de paginação
sem medir nada útil.

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
  Services/StressService.cs       carga de CPU controlada
  Views/SensoresWindow.xaml       lista de todos os sensores
  Services/ConsumoService.cs      registro de consumo por dia, em txt
  Services/GpuProcessService.cs   uso da placa de vídeo por programa
  Views/GpuProcessosWindow.xaml   a tela de GPU por programa
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

A medição de disco por processo é a parte cara, e por isso só corre com a aba
Disco aberta, fora da thread da interface e com uma varredura por vez. O caminho
do executável sai de `QueryFullProcessImageName`, uma chamada por processo, e
apenas para os que aparecem na lista — `Process.MainModule` enumeraria os
módulos carregados de cada um dos trezentos processos da máquina.

## De onde vem a escala das barras

Uma barra pela metade só informa alguma coisa se der para saber metade do quê.
Por isso cada barra mede o valor de agora contra o que a peça aguenta, e não
contra o maior valor que ela já fez. O teto vem da melhor fonte disponível, nesta
ordem, e o tooltip da barra sempre diz qual delas está valendo:

| Origem | O que é | Onde aparece |
|---|---|---|
| Manual | Número informado na tela de componentes | Sobrepõe todas as outras |
| Hardware | A peça publica o limite, ou ele foi medido nela | Limiares SMART dos discos, ventoinha calibrada, VRAM total |
| Extrapolado | Calculado a partir de dois sensores da mesma peça | Rotação máxima projetada pela curva PWM |
| Catálogo | Ficha técnica do modelo | PPT da CPU, TBP da GPU, TjMax, clock de boost |
| Medido | O maior valor já registrado, que sobe com o tempo | Taxas de disco; último recurso para o resto |

Barras com teto real hoje: potência e temperatura da CPU, cada ventoinha da
placa-mãe, VRAM, potência, ventoinha e temperatura de memória da GPU, e leitura,
escrita e temperatura por unidade de disco.

Um canal da placa-mãe só é reconhecido como ventoinha depois de ser visto
girando. Girar é a única prova de que há algo espetado no conector: a leitura do
momento não serve, porque a primeira leitura de um chip de placa-mãe é a menos
confiável e conectores vazios podem devolver lixo nela. O conjunto só cresce, e
por isso uma ventoinha em modo silencioso entra assim que acorda e continua
listada quando volta a parar — aí aparece como "parada", em vez de sumir e fazer
a aba pular de altura.

As barras de ventoinha na aba CPU são controladas em **Ajustes → No painel**. A
chave liga a visualização, e com ela ligada aparece a lista das ventoinhas
reconhecidas, cada uma com a rotação de agora — que é como se descobre qual
conector é qual, já que "Fan #3" não diz nada. Desmarcadas, elas continuam sendo
lidas: é dessa leitura que saem as curvas de PWM, a calibração e a tela de todos
os sensores. Só a barra some.

A configuração guarda as **ocultas**, não as visíveis. Assim a lista vazia
significa "todas", uma ventoinha nova aparece sozinha, e desmarcar todas
continua querendo dizer nenhuma. A potência, o clock e a temperatura
nos quadrados da CPU e da GPU trazem o mesmo contexto no tooltip, porque o
quadrado é estreito demais para `253 / 253 W` sem atropelar.

### O catálogo

`Dados/catalogo.json` viaja embutido no executável e guarda potência máxima,
limite térmico e clock de boost por modelo. Ele é atualizável pela internet — um
GET de poucos kilobytes no próprio repositório, no máximo uma vez por semana,
com cache local e falha silenciosa. Desligado nas configurações, vale a cópia
embutida. Não existe API pública gratuita de especificações de hardware: a
TechPowerUp licencia a dela, então o catálogo é mantido à mão aqui.

A medição também serve de conferência: uma peça que ultrapassa a ficha técnica
denuncia que aquele exemplar tem limite elevado de fábrica, e a escala passa a
seguir o que foi medido.

### Por que ventoinha não entra no catálogo

Nenhuma tabela sabe qual ventilador está espetado em qual conector da placa, e a
rotação varia entre fabricantes da mesma placa de vídeo. Então a rotação máxima
sai dos próprios sensores, de duas formas:

- **Passiva**: o app casa cada sensor de rotação com o canal de PWM que o comanda
  (`Fan #2` com `Fan Control #2`; nas placas de vídeo, pelo nome igual) e guarda
  os pontos da curva entre sessões. Com o PWM acima de 60%, projeta a reta até
  100%. Abaixo disso não projeta nada: a curva não é reta, e extrapolar de 40%
  multiplicaria o erro por seis.
- **Calibração**: em Componentes → Calibrar ventoinhas, todas sobem a 100% por
  cerca de doze segundos e voltam sozinhas ao controle da placa-mãe. A rotação
  medida aí é a resposta definitiva. A restauração é garantida por `finally`, pelo
  cancelamento da tela e de novo no fechamento do app.

Ventoinhas em controladora externa ou travadas pela BIOS aparecem na lista como
não comandáveis: a rotação é visível, mas o teto não pode ser medido.

## Registro de consumo

`%APPDATA%\MonitorPC\consumo.txt` guarda o consumo de cada dia: kWh, tempo com o
app aberto, média e pico. O botão na aba Energia abre o arquivo. Ele é gravado
junto com os sensores lentos, de 30 em 30 segundos, e de novo ao fechar o app.

É um arquivo só, com a parte de cima para ler e a de baixo para o app reabrir na
próxima sessão. Dois arquivos seriam mais fáceis de programar e um deles ficaria
desatualizado sem ninguém perceber.

Duas ressalvas que o arquivo e a tela repetem, porque sem elas o número engana:

- **É estimativa.** Só CPU e placa de vídeo vêm de sensor. Pentes de RAM,
  discos, ventoinhas, placa-mãe e LEDs entram pelos watts por unidade
  configurados em Componentes de energia.
- **Só conta com o app aberto.** O que o PC gasta com ele fechado não entra e
  não há como recuperar depois. Por isso a coluna "aberto" fica ao lado do kWh:
  sem ela, 0,8 kWh não diz se foram três horas ou doze.

O valor é o consumo dos componentes. Para a tomada, divida pela eficiência da
fonte — é o que o campo "Na tomada" faz.

## GPU por programa

Não existe sensor para isto. A placa informa a ocupação dela como um todo; quem
sabe repartir por processo é o agendador gráfico do Windows, que publica o
resultado nos contadores de desempenho `GPU Engine` e `GPU Process Memory` — os
mesmos que o Gerenciador de Tarefas lê. Funciona com qualquer placa, AMD,
NVIDIA ou integrada, sem biblioteca de fabricante e sem administrador.

A leitura é feita com PDH, uma consulta só com instância curinga, e os nomes dos
contadores são pedidos em inglês via `PdhAddEnglishCounter`: num Windows em
português os objetos aparecem traduzidos, e um caminho montado com o nome
traduzido só funcionaria naquele idioma.

A porcentagem mostrada é a do motor mais ocupado por aquele programa, não a soma
de todos — somar daria mais de 100% num jogo que usa 3D e Copy ao mesmo tempo, e
é assim que o Gerenciador de Tarefas também conta. O motor aparece ao lado, e é
ele que separa um jogo (3D) de um minerador (Compute) ou de uma chamada de vídeo
(VideoEncode).

## Tema claro e escuro

Toda cor vem de uma chave de recurso trocada em tempo de execução pelo
`ThemeService`, e as telas referenciam essas chaves por `DynamicResource`. Duas
regras importam para quem for mexer:

- Em componentes montados em código, use `SetResourceReference`, nunca
  `ThemeService.Pincel(...)` dentro de um inicializador de objeto. O pincel pego
  no construtor congela o tema daquele instante, e componentes de vida longa —
  as barras, a grade de núcleos, as linhas de disco — sobrevivem à troca de tema.
- Nada de cor literal no XAML, exceto branco sobre o acento (ícone do botão de
  energia, texto de aba selecionada), onde o fundo é o acento nos dois temas.

As chaves de aviso (`AvisoFundo`, `AvisoBorda`, `AvisoIcone`) invertem no tema
claro: o laranja escuro que separa um cartão de alerta no escuro vira uma mancha
preta no branco, então a versão clara usa fundo creme com traço forte.

## Hardware de referência

Ryzen 7 5700, ASUS Prime B550M-A, Radeon RX 7600, 24 GB em 3 módulos, 3 SSDs.

## Conflitos conhecidos

HWMonitor, Armoury Crate e Ryzen Master disputam o mesmo acesso de baixo nível aos
sensores. Rodando junto com eles, algumas leituras podem vir zeradas.
