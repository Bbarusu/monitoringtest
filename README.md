# KInfo

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

## Kisland (a ilha no topo da tela)

Para quem usa, o nome é **Kisland**, inspirada na ilha dos iPhones mais novos.
No código o nome continua "ilha" (`IlhaWindow`, `Ilha*` na configuração):
renomear tudo mudaria as chaves do `config.json` e jogaria fora o que cada
pessoa já configurou.

### Tamanho

O tamanho (`IlhaEscala`, 70 a 100%, padrão 85) é um `LayoutTransform` na
Kisland inteira, para texto, cantos e ícones encolherem juntos.

Houve um efeito vidro, inspirado no Liquid Glass, entre a 0.1.13 e a 0.1.17.
Saiu: o desfoque do Windows atrás de uma janela de formato arredondado não
ficou bom em nenhuma das duas tentativas (janela transparente desfoca o
retângulo todo; janela comum recortada perde a suavização das bordas e a
sombra). A chave `IlhaVidro` de configurações antigas é ignorada.

### Contador de FPS

`FpsService` conta os eventos de "quadro apresentado" que o DXGI (DirectX 10
a 12) e o D3D9 publicam no rastreamento de eventos do Windows — o caminho do
PresentMon. Não injeta nada no jogo. Usa a
`Microsoft.Diagnostics.Tracing.TraceEvent`; a sessão (`KInfo-FPS`) só existe
com o contador ligado e a Kisland ativa, e exige o app elevado. Jogos só em
Vulkan ou OpenGL não passam por esses provedores.

A conta segue a do PresentMon: a hora de cada quadro vem do próprio evento, e
o FPS é a média dos intervalos do último segundo. Contar quantos eventos
chegavam a cada segundo de relógio oscilava muito, porque o Windows entrega os
eventos em lotes. Os quadros são separados por cadeia de troca (swap chain), e
vale a mais rápida do processo: somar todas inflava o número quando o jogo
tinha mais de uma. Presents só de teste (`DXGI_PRESENT_TEST`), que não mostram
nada, ficam fora. A Kisland ainda suaviza de leve variações pequenas.

### Calendário e atalhos

Fechada, a Kisland pode mostrar a data junto do relógio ("Qui 29 15:49"). Aberta,
mostra um calendário do mês, no jeito do widget da tela de espera do iPhone,
refeito só na virada do dia. Os nomes de mês e dia estão no código: a cultura
pt-BR do Windows abrevia com ponto e em minúscula.

O anel pulsante pode virar atalho para um chat de IA (ChatGPT, Claude, Gemini,
Copilot, Perplexity, DeepSeek ou um endereço próprio). Com um escolhido, ele
fica sempre no começo da pílula. A Kisland aberta tem ainda até seis apps ou
sites (`IlhaAtalhos`). Como o KInfo costuma rodar elevado, `AtalhoService` abre
tudo pelo Explorer, para o navegador e os apps não herdarem o administrador.

Com `IlhaIaPreferirApp`, o anel abre o app do chat quando ele está instalado: a
busca é pelo nome na lista de apps do menu Iniciar (`shell:AppsFolder`, lida
pelo `Shell.Application` e guardada por 10 minutos), o que pega app da Microsoft
Store, instalador próprio e app da web instalado pelo navegador; abre com
`explorer shell:AppsFolder\<id>`. Sem o app, abre o site.

Abrir ao passar o mouse espera `IlhaEsperaParaAbrirMs` (5 s por padrão) com o
cursor em cima, para dar tempo de clicar no anel ou num item. Na Kisland
aberta, os cartões de CPU, GPU, RAM, disco e energia ligados se redistribuem
nas duas fileiras: até três numa só, quatro em dois e dois, cinco em dois e três.

### Telas OLED

O tema `Preto` é o escuro com as superfícies pretas (`ThemeService.Preta`:
fundo, borda, cartão, chip e trilho); texto, ícones e avisos ficam os do
escuro. Ele desliga a transparência (`AppConfig.TransparenciaAtiva`), que
mostraria o desktop no lugar do preto. No fim do enum, para não mudar o
número dos outros temas na configuração salva.

A Kisland preta pendura `ThemeService.PaletaPreta()` nos recursos da própria
janela, por cima dos do app: tudo nela usa `DynamicResource`, então ela fica
preta em qualquer tema, sem borda nem sombra.

A proteção contra marca (`IlhaProtegerTela`, ligada por padrão) faz três
coisas: a cada 3 minutos a Kisland anda 1 ou 2 px num ciclo de oito posições;
sem o mouse a 60 px dela por `IlhaEscurecerMinutos`, a casca vai a 35% de
opacidade e volta ao chegar perto, ao abrir ou num aviso; e com 10 minutos sem
entrada no PC (`GetLastInputInfo`) ela se esconde e volta no primeiro
movimento. O Windows não informa se a tela é OLED, então nada disso depende de
detectar a tela.

### Kdrop

A bandeja aberta para a casa (`KdropService`), só na rede local e sem serviço
de fora:

- Servidor HTTP (`HttpListener`) na porta 48620. Serve a página do celular
  (`Dados/kdrop.html`, recurso embutido): a bandeja vista de fora, com os PCs
  da casa, os arquivos para baixar e o envio do celular para o PC.
  `GET /api/estado` (o celular pergunta a cada 2 s e assim aparece na Kisland),
  `GET /api/arquivo/{id}`, `POST /api/pedido`, `GET /api/pedido/{id}` e
  `POST /api/enviar/{pedido}?nome=`.
- Anúncio UDP na porta 48621, a cada 5 s, para os KInfo da rede se acharem.
- O envio para outro PC é o mesmo caminho do celular: pede, espera o aceite,
  envia arquivo por arquivo.

Todo pedido passa pelo aceite na Kisland (prazo de 60 s), a não ser de aparelho
em `KdropConfiaveis`. Só entra arquivo que estava no pedido aceito, com o nome
limpo, e nunca sobrescreve: repetido vira "foto (2).jpg". Vai para
`KdropPasta` (Downloads\Kdrop por padrão) e entra na bandeja. HTTP comum: com
criptografia seria preciso certificado, e o celular avisaria de site inseguro.
Na primeira vez o KInfo cria a regra "KInfo Kdrop" no Firewall, só para redes
privadas e de domínio. O `HttpListener` em todas as interfaces exige o app
elevado. QR Code pelo QRCoder.

Arrastar um arquivo da bandeja até um aparelho é feito à mão, com o mouse preso
ao item: o arrasto do Windows não serve, porque a Kisland cancelou o registro
dela como destino (veja a bandeja). Saindo da Kisland, vira o arrasto normal.

### Roda do mouse, saídas de som e bandeja

A roda e o clique do meio chegam por mensagem do Windows (`WM_MOUSEWHEEL`,
`WM_MBUTTONUP`), lidas num gancho da janela antes do WPF, que nem sempre entrega
a roda a uma janela sem foco. Cada clique da roda muda 2%, o passo do Windows;
touchpads mandam pedaços menores, que se somam. Depende da opção do Windows
"rolar janelas inativas ao passar o mouse", que vem ligada. No modo jogo não age.

As saídas de som são os aparelhos ativos do Core Audio, com o nome do painel de
som. Trocar a padrão usa `IPolicyConfig`, a interface não documentada do próprio
painel de som, a mesma dos apps de troca de áudio; a troca vale para os três
papéis (console, multimídia e comunicação).

A bandeja recebe arquivos por `WM_DROPFILES` (`DragAcceptFiles`), e não pelo
arrastar do WPF: o Windows não deixa o Explorer arrastar para um app elevado
pelo OLE, e pelo caminho antigo deixa, liberadas `WM_DROPFILES`, `WM_COPYDATA` e
`WM_COPYGLOBALDATA` com `ChangeWindowMessageFilterEx`. O WPF registra toda
janela como destino OLE ao criá-la, e com esse registro o Windows nunca tenta
o caminho antigo; por isso a Kisland cancela o registro (`RevokeDragDrop`)
assim que nasce. Pelo mesmo motivo ela não pode ter elementos com `AllowDrop`. Guarda só os caminhos, até 12, e
só enquanto o app está aberto. Clique abre pelo Explorer; arrastar para fora usa
o OLE, que com o app elevado pode não chegar ao Explorer — para isso há o
copiar, que põe os arquivos na área de transferência para colar numa pasta.

`DetectorDeJogo` decide quem é jogo: quem entrega pelo menos 10 quadros por
segundo, não está na lista de programas que desenham muito sem serem jogos
(navegadores, Discord, OBS, players de vídeo…) e tem uma janela visível que
cobre o monitor, está maximizada ou ocupa metade dele — em qualquer monitor,
com ou sem foco.

Com o jogo em tela cheia sem borda no mesmo monitor da Kisland, ela entra no
modo jogo: o FPS, um aviso de temperatura se houver, e o que estiver marcado
em "No modo jogo, ela mostra" (`IlhaJogo*`: temperatura e uso de CPU e GPU,
RAM, potência, relógio e música; por padrão, as temperaturas de CPU e GPU). Não
abre ao passar o mouse, e a roda e o clique do meio não agem. Tela cheia em outro monitor não a esconde mais. Tela cheia exclusiva
continua escondendo: nada aparece por cima, e o Windows não diz em qual monitor
ela está.

### Guia

`GuiaKislandWindow` são nove passos com prévia desenhada e o controle de
verdade: o que se mexe ali grava na configuração na hora. Aparece uma vez só,
pelo convite que o painel mostra na primeira abertura de uma versão que tem o
guia (`GuiaKislandVisto`), para quem instalou agora e para quem atualizou. Fica
marcado como visto no momento em que aparece: ignorar também é resposta. Para
rever, Personalização → Ver o guia da Kisland.

### Funcionamento

Uma pílula no centro do topo da tela, ligada em **Personalização → Ilha no topo
da tela**. Fechada, mostra o que está tocando, as temperaturas de CPU e GPU e o
volume — cada item pode ser desligado, e há também o uso de RAM. Ao passar o
mouse (ou ao clicar, conforme a escolha) ela abre: player com capa, tocar,
pausar, anterior e próxima, barra de progresso, as temperaturas em destaque,
volume com mudo, os perfis de energia e atalhos para o painel e a
Personalização.

Vem desligada. Uma janela sempre por cima de tudo não pode surgir sozinha numa
atualização automática.

Arrastar a ilha a leva para outro monitor, sempre para o topo dele e
centralizada; a escolha fica em `IlhaMonitor`, pelo nome do dispositivo do
Windows. Monitor desligado faz a ilha ir para o principal sem apagar a escolha.
A posição é feita em pixels, por `SetWindowPos`, e não por `Left`/`Top` do WPF:
entre monitores de escalas diferentes as unidades do WPF mudam no caminho e a
conta de centralizar erraria no monitor de destino.

Abrir ao passar o mouse é conferido pela posição do cursor na tela, dez vezes
por segundo e só com a ilha visível. Por `MouseEnter`/`MouseLeave` não
funcionava: a ilha nunca é a janela ativa, e ao mudar de tamanho o WPF dá o
cursor como fora até ele se mexer — ela abria e fechava em seguida.

Música pausada por três minutos sai da ilha; volta ao tocar ou quando o player
anuncia outra faixa. Sem nada para mostrar, a pílula vira um círculo com um anel
que respira devagar, a doze quadros por segundo.

Não tem aparência própria: segue a cor de destaque, o tema e a transparência do
painel, e mexer na Personalização repinta as duas.

Nenhuma leitura nova de sensor. As temperaturas são as mesmas que desenham o
ícone da bandeja; música e volume chegam por aviso do Windows:

- **O que está tocando** vem do controle de mídia do sistema
  (`GlobalSystemMediaTransportControlsSessionManager`) — o mesmo que põe a
  música no painel de volume. Funciona com qualquer player que se anuncie ao
  Windows: Spotify, navegador, Media Player. É por causa dele que o app passou
  a mirar `net8.0-windows10.0.19041.0`, com mínimo no Windows 10 1809.
- **O volume** vem do Core Audio (`IAudioEndpointVolume`), com aviso de
  mudança. Quando ele muda, a ilha mostra o nível por dois segundos. O aviso
  do próprio Windows continua aparecendo: não existe forma suportada de outro
  app desligá-lo.

O único relógio próprio é de um segundo, para sumir em **tela cheia** e andar a
barra de progresso com a ilha aberta. Tela cheia é conferida de dois jeitos,
porque nenhum cobre tudo sozinho: o estado que o Windows publica
(`SHQueryUserNotificationState`, que pega Direct3D exclusivo e apresentação) e
a geometria da janela em primeiro plano, que pega jogo em janela sem borda e
vídeo em tela cheia no navegador. Janela maximizada com barra de título não
conta, senão a barra de tarefas em ocultar automaticamente faria a ilha sumir
a cada janela maximizada.

A ilha nunca toma o foco (`WS_EX_NOACTIVATE`): tocar, pausar ou mexer no volume
não tira o cursor do que estava sendo digitado.

## Código num arquivo só

`codigo-unico.ps1` junta todo o código-fonte em
`dist\instalador\KInfo-codigo-completo.md`, ao lado do instalador. O
`publicar.cmd` o chama no último passo, para o arquivo nunca ficar defasado em
relação ao executável que saiu junto.

O cabeçalho explicativo mora em `arte/cabecalho-codigo.md`, e não dentro do
script: assim o `.ps1` fica em ASCII puro e a prosa acentuada vive num arquivo
lido explicitamente como UTF-8, sem depender da página de código do console.

## Ícone

`arte/gerar-icone.py` desenha `src/MonitorPC/kinfo.ico` — o contorno de um chip,
branco, sobre o quadrado azul do acento. É um script e não um arquivo desenhado
à mão porque cada tamanho precisa de proporções próprias: o mesmo desenho
reduzido de 256 para 16 px vira borrão. Conforme o ícone encolhe, o traço
engrossa, os pinos caem de oito para quatro, o canto do corpo perde raio para
não virar um anel, e abaixo de 24 px o miolo sai de cena — com ele sobraria um
pixel de folga, e um pixel de folga não se enxerga.

O `.ico` é montado quadro a quadro em vez de pelo `save` do Pillow, que guarda
uma imagem só e reduz as outras, jogando fora justamente esse ajuste.

Na bandeja o ícone continua sendo o número da temperatura desenhado a cada
leitura. O chip é a marca do app: janela, atalho, menu Iniciar e instalador.

## Instalador

`publicar.cmd` na raiz gera um instalador de arquivo único em
`dist\instalador\KInfo-Setup.exe`. Sem argumento, o .NET viaja dentro e o
instalador roda em qualquer Windows 10 ou 11 de 64 bits (~130 MB);
`publicar.cmd leve` gera a versão de poucos megabytes, que exige o .NET 8
Desktop Runtime na máquina de destino.

O instalador tem a mesma cara do app — painel escuro arredondado, mesma paleta e
tipografia — e faz quatro coisas:

- Copia o app para `C:\Program Files\KInfo`
- Registra uma **tarefa do Windows com privilégio elevado**, que é o que resolve
  o problema do driver (abaixo)
- Cria os atalhos e, opcionalmente, o início junto com o Windows
- Registra a desinstalação no Painel de Controle. O próprio instalador é o
  desinstalador, copiado para a pasta de instalação

### Atualização

Quando o app baixa uma versão nova, ele abre o instalador com
`--atualizar --tema=… --acento=#RRGGBB`. Com uma instalação anterior no lugar,
o instalador vira o painel de atualização: nas cores e no tema do app, sem a
tela de opções, já começando, com as notas da versão (embutidas pelo projeto) e
reabrindo o app no fim. Rodado à mão, ele continua mostrando as opções — é o
jeito de mudar alguma.

As opções de cada instalação ficam gravadas na chave de desinstalação do
registro. Instalações anteriores a isso não gravaram nada, e aí o instalador
deduz pelo que a máquina mostra: a tarefa, o gatilho de logon, o atalho, a
exceção do Defender. O PawnIO é a exceção — ausente pode ser recusa —, então ele
não é instalado por dedução; o fim da atualização avisa se ele faltar.

A confirmação do Windows não se pula por configuração: o instalador escreve em
Arquivos de Programas. Mas o app instalado com acesso completo aos sensores já
roda elevado, e um processo elevado abre outro elevado sem perguntar. Para essa
instalação, que é a comum, atualizar não mostra aviso nenhum.

A cor vai pela linha de comando, e não pela leitura do `config.json`, porque o
instalador elevado com a senha de outra conta de administrador enxergaria a
pasta de dados da outra conta.

### O driver, e por que a tarefa agendada

Ler temperatura de CPU e placa-mãe exige duas coisas, e por muito tempo este
README só documentava uma. As duas: o **PawnIO** instalado no sistema, e
**privilégio elevado** para falar com ele. Faltando qualquer uma, essas leituras
voltam vazias.

O PawnIO é um driver assinado, instalado uma vez, fora do app. A
`LibreHardwareMonitorLib` **não embute driver nenhum**: ela usava a família
WinRing0, que tem falha conhecida de 2020 (CVE-2020-14979) e hoje está na lista
de drivers bloqueados do Windows, e migrou para o PawnIO. Como ela não reclama
quando o PawnIO falta — devolve a lista de sensores vazia e segue —, a ausência
não deixa rastro nenhum: nenhum arquivo escrito, nenhum serviço criado, nenhuma
recusa no log do Windows, nenhuma detecção de antivírus.

Isso produz o "funciona na minha máquina" mais limpo possível: num PC de
desenvolvimento o PawnIO já está lá, posto pelo LibreHardwareMonitor oficial em
algum momento, e numa máquina limpa não está. Foram três máquinas até a causa
aparecer. O instalador agora oferece baixá-lo de pawnio.eu e instalar, e o painel
nomeia a dependência quando ela falta.

Baixar em vez de embutir é deliberado: o PawnIO é GPL-2.0. Carregá-lo pela
interface de IOCTL está coberto pela exceção de licença dele, mas redistribuir o
instalador dentro do nosso seria redistribuição de GPL, com a obrigação de
acompanhar o código-fonte.

Pedir elevação pelo manifesto do app resolveria, mas cobraria a confirmação do
Windows **toda vez** que o app abrisse — e este app abre junto com a sessão. Em
vez disso, o instalador registra uma tarefa agendada com `RunLevel
HighestAvailable`, autorizada uma única vez durante a instalação. Quando o app
percebe que abriu sem privilégio e que a tarefa existe, ele pede à tarefa para
subir uma cópia elevada e sai. Sem instalação não há tarefa, e aí o app roda como
sempre rodou — `dotnet run` no desenvolvimento continua igual.

O argumento `--elevado`, que a tarefa passa, impede laço infinito: com ele o app
nunca tenta se relançar.

### Windows Defender

O Defender marca a família de drivers WinRing0 como `HackTool:Win32/Winring0`.
A biblioteca **não** carrega mais esse driver — hoje é o PawnIO, assinado, e em
três máquinas medidas o Defender não mexeu com ele nem com a nossa pasta.

O que foi observado, e cuja causa nunca foi confirmada: a
`LibreHardwareMonitorLib.dll` desapareceu de `bin\Debug` uma vez durante o
desenvolvimento. Quando a DLL falta o app simplesmente para de abrir, sem
nenhuma mensagem que ligue uma coisa à outra. A exceção do Defender para a pasta
de instalação fica como precaução contra isso, não como correção de um problema
diagnosticado. Pelo mesmo motivo o `publicar.cmd` falha
com uma mensagem explícita se a DLL não aparecer na publicação.

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

Gerada na primeira execução em `%APPDATA%\KInfo\config.json`.

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

A pasta de dados era `%APPDATA%\MonitorPC` quando o app se chamava Monitor de
PC. O app renomeia a pasta na primeira abertura, levando junto o consumo e as
curvas de ventoinha aprendidas — o que ele mais demorou para saber.

`%APPDATA%\KInfo\consumo.txt` guarda o consumo de cada dia: kWh, tempo com o
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

A tela abre pelo cartão **GPU** na aba Geral, ao lado do de RAM: são a mesma
pergunta sobre peças diferentes. Cada linha tem um X que pede ao programa para
fechar, como o X da janela dele — nada é forçado, e um jogo em tela cheia
costuma ignorar o pedido. O pedido vai pelo PID, e não pelo nome: um navegador
tem muitos processos e só um costuma estar usando a placa. Processos do sistema
e de segurança aparecem na lista, para a conta fechar, mas sem o botão.

A lista congela enquanto o cursor está sobre ela. A ordem muda a cada leitura, e
um botão de fechar que se move debaixo do cursor faria o clique cair no programa
errado.

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
