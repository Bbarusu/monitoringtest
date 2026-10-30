# KInfo — resumo para a implementação

Base consolidada a partir do design no canvas e dos testes de sensores feitos em 21 e 22/10/2026
no PC de referência (Ryzen 7 5700, ASUS Prime B550M-A, Radeon RX 7600, 24 GB, 3 SSDs).

---

## 1. O que o app é

Painel de monitoramento que vive na bandeja do Windows. Abre no **canto inferior direito**,
ancorado ao ícone da barra de tarefas, respeitando a área de trabalho útil (não fica sob a barra)
e seguindo o lado em que a barra estiver. Largura fixa de 392 px de conteúdo, altura variável.

Princípio que guia tudo: **monitorar o máximo possível sem consumir recursos**.

---

## 2. Sensores validados no hardware de referência

Fonte: LibreHardwareMonitorLib 0.9.6, .NET 8 (`net8.0-windows`), com driver de acesso em modo kernel.

### Funcionam
| Área | Métricas confirmadas |
|---|---|
| CPU | Tctl/Tdie, potência do pacote, clock médio, clock e potência por núcleo, uso total, uso por thread |
| GPU | Temperatura da borda, memória e hot spot, potência, clocks, VRAM usada, uso, ventilador |
| RAM | Uso total, usado e disponível, módulos individuais com timings e capacidade |
| Discos | Temperatura, leitura e escrita em MB/s, atividade, vida útil, horas ligado, espaço livre |
| Placa-mãe | Chip Nuvoton NCT6798D: 7 canais de ventoinha, 6 temperaturas, tensões |

### Limitações encontradas
- Apenas **2 dos 7 canais de ventoinha** reportam rotação. Os demais podem estar vazios ou ter
  ventoinhas de 3 pinos sem retorno de rotação.
- As temperaturas da placa-mãe vêm **sem nome** (`Temperature #1` a `#6`) e precisam ser
  identificadas manualmente ou rotuladas pelo usuário.
- A CPU expõe clocks por **núcleo** (8) e cargas por **thread** (16). A interface precisa tratar
  os dois níveis.

### Armadilhas já resolvidas (não repetir)
1. **Temperatura do disco:** os SSDs expõem também os limiares do fabricante
   (`Warning Temperature`, `Critical Temperature`). Pegar o maior valor retorna 88 °C fixo.
   Filtrar esses nomes e os sensores secundários do controlador (`Temperature #2`).
2. **Taxas e atividade de disco:** exigem **duas leituras** para gerar o delta. Uma leitura só
   retorna `n/d` ou 100%.
3. **Ordem dos sensores de ventoinha não é estável.** Fixar cada ventoinha pelo **nome** do sensor,
   senão as colunas trocam entre leituras.
4. **Driver:** sem ele, CPU e placa-mãe retornam zero e o chip Super I/O nem é detectado, mesmo
   com privilégio de administrador. Discos, GPU e RAM continuam funcionando, o que torna a falha
   silenciosa e fácil de diagnosticar errado.

---

## 3. Custo medido do monitoramento

Lendo todos os sensores a cada 3 segundos:

| Métrica | Resultado |
|---|---|
| CPU do processo | média 0,05 a 0,07%, pico 0,16% |
| Memória | estabiliza em torno de 88 MB |

Observação: o `WorkingSet` do processo cai drasticamente (de 89 para 6 MB) quando o Windows
apara a memória sob pressão de outro processo pesado. Medir também a **memória privada**, que
não sofre esse corte, senão o número engana.

---

## 4. Estratégia de leitura

| Situação | Intervalo |
|---|---|
| Painel fechado | 3 a 5 s, só o necessário para o ícone da bandeja e os alertas |
| Painel aberto | 1 s |
| Sensores lentos (vida útil, espaço livre) | 30 a 60 s |

Histórico em buffer circular na memória, cobrindo os últimos 5 minutos, para os mini gráficos.
Gravação em disco é opcional e em intervalo maior.

---

## 5. Telas

Cinco abas, sempre na mesma posição para não "pularem" ao trocar: **Geral, CPU, GPU, Disco, Energia**.
Não há aba de Rede; o monitoramento de rede foi removido do escopo.

Padrão visual comum a todas: cabeçalho com nome e dois chips, anel com o valor principal e quatro
métricas ao lado, gráfico dos últimos 5 minutos, barra de abas, bloco de detalhe, rodapé de status
com os ícones de personalização e configurações.

### Geral
Temperatura da CPU em destaque no anel, chips de temperatura e uso de RAM, cards expansíveis de
GPU e RAM, e os três perfis de energia.

### CPU
A grade de núcleos é **montada a partir do que o PC informa**, nunca fixa. Seletor entre Núcleos e
Threads, porque a contagem varia muito entre processadores. Sem o nome do modelo no cabeçalho.

### GPU
Temperatura da borda, hot spot, clocks, VRAM, potência e ventilador. **Alerta de hot spot** quando
a diferença para a borda for alta (no PC de referência chegou a Δ25 °C, com 89 °C de hot spot),
sinalizando possível pasta térmica ressecada.

### Disco
Temperatura mais alta entre as unidades, leitura e escrita, vida útil e horas ligado. Lista dos
processos que mais consomem o disco agora, com PID, e botão para o log detalhado.

### Energia
Potência estimada no anel, comparada com a capacidade da fonte. Consumo acumulado em Wh,
média e pico. Barra mostrando para onde vai a energia, separando o que é **medido** (CPU e GPU)
do que é **estimado**.

---

## 6. Telas auxiliares

### Configuração inicial
Define o limite de temperatura de CPU, GPU e Disco, cada um com controle deslizante mostrando a
temperatura atual. Opções de notificação do Windows e de sugerir o Modo Econômico ao ultrapassar.

### Alerta de temperatura
Dispara quando a temperatura passa do limite **definido pelo usuário**. O anel fica laranja e surge
um aviso com o valor atual e o limite, com os botões "Ativar Econômico" e "Silenciar 10 min".

### Perfis de energia
Econômico, Equilibrado e Desempenho, trocando o plano de energia do Windows.

O **Modo Desempenho** tem tela própria: limpar caches temporários, liberar RAM, priorizar CPU e GPU,
e fechar processos secundários. A lista de processos aparece com caixas de seleção — o usuário
escolhe o que fecha. Antivírus, drivers e processos do Windows **nunca** são fechados. Ao voltar
para Equilibrado, tudo é restaurado.

### Log de disco
Horário, processo, PID, unidade, leitura ou escrita, taxa e duração, com filtros e destaque para
picos. Exportação em `.txt` e opção de limpar.

### Rastreamento avançado (opcional, desligado por padrão)
Mostra qual arquivo cada processo acessa. Antes de ativar, a tela avisa as consequências: mais uso
de CPU, mais memória e gravação, possível queda de desempenho em jogos, e pedido de permissão de
administrador. O usuário escolhe a duração (15 min, 1 hora, ou até parar) e pode pausar durante
jogos em tela cheia. Gera log em `.txt` com **nome e caminho** dos processos que lideram o consumo,
acima de um limite em MB/s que o usuário define, na pasta que escolher.

### Personalização
Acompanhar a cor de destaque do Windows (ligado, desativa a escolha manual), ou escolher entre
seis cores. Ligar ou desligar transparência e ajustar a opacidade de 40 a 100%, com prévia.

### Kisland (ilha no topo da tela)
Pílula no centro do topo, no mesmo visual do painel. Fechada: música tocando (capa e equalizador),
temperatura de CPU e GPU, volume e, opcional, uso de RAM; um chip laranja aparece enquanto houver
alerta de temperatura. Aberta (ao passar o mouse ou ao clicar): player com capa, controles e progresso,
cartões de temperatura que ficam laranja acima do limite, volume com mudo, os três perfis de energia e
atalhos para o painel e a Personalização. Avisos rápidos: volume por 2 s ao mudar, faixa nova por 3 s.
Some em tela cheia. Ligada e configurada na Personalização; desligada por padrão.

Data no chip do relógio, na cor de destaque, como na tela de espera do iPhone. Calendário do mês
na Kisland aberta: mês e ano em texto fraco, dia da semana na cor de destaque, o dia em número leve
grande, e a grade do mês com hoje num círculo da cor de destaque. O anel pulsante abre um chat de IA
quando há um escolhido. Os atalhos de apps e sites ficam em círculos com o ícone do app ou a
inicial do site na cor de destaque. Ícone de volume pequeno, de cantos redondos e traço 1.6,
desenhado sempre no mesmo tamanho, com ou sem as ondas.

Saídas de som em botões de canto 13 sob o volume, a atual nas cores do perfil escolhido (fundo
InfoFundo, borda na cor de destaque); ícone de fone, monitor ou caixa pelo nome do aparelho. Bandeja
de arquivos num minicartão: título com a contagem, copiar e esvaziar em botões redondos, e os
arquivos em quadros de canto 12 com o ícone do Windows; na pílula, um chip com a contagem.

### Guia da Kisland
Convite único no painel (cartão "Novidade", botões "Depois" e "Bora ver") e guia de nove passos, em
linguagem próxima: "Oi, essa é a Kisland", "Passou o mouse, ela abre", "Você escolhe o que aparece",
"O mês inteiro à vista", "O circulozinho virou atalho", "Som e arquivos na ponta do mouse",
"Funciona em qualquer tela", "Tá jogando? Ela conta o FPS", "Combina com o seu KInfo". Cada passo tem prévia e o controle real.

### Componentes de energia
As quantidades são **detectadas do hardware**; o usuário ajusta à mão apenas o que o PC não enxerga,
como ventoinhas em controladora externa, peças com LED e bomba de water cooler. Também define
capacidade e eficiência da fonte, e pode informar a leitura de um wattímetro de tomada para calibrar.

---

## 7. Modelo de configuração

Nada de valores fixos no código. Um arquivo de configuração guarda:

- **Watts por unidade:** módulo de RAM, disco, ventoinha, peça com LED, bomba, placa-mãe, outros.
- **Quantidades:** cada uma com um campo opcional. Vazio significa detectar; preenchido, o valor
  manual passa a valer sobre a detecção.
- **Fonte:** capacidade e eficiência.
- **Limites de temperatura** definidos na configuração inicial.
- **Preferências visuais:** cor, transparência, opacidade.

A interface sempre mostra a **origem** de cada quantidade, distinguindo o que foi detectado do que
foi informado à mão.

Cálculo de energia: `Wh = potência x tempo`, somado a cada leitura. CPU e GPU são medidas; o
restante é estimativa. O valor na tomada aplica a eficiência da fonte.

---

## 8. Requisitos técnicos

- **Plataforma:** .NET 8 (`net8.0-windows`), interface nativa em WPF. Sem navegador embutido —
  fechou o painel, a renderização para de consumir.
- **Biblioteca de sensores:** LibreHardwareMonitorLib 0.9.6.
- **Driver:** o **instalador** registra o serviço do driver uma vez, com a elevação pedida ali.
  Assim o app abre sem exigir administrador a cada execução e não depende de outro programa ter
  instalado o driver antes. Falta validar se o acesso se mantém após reiniciar o PC.
- **Conflito conhecido:** HWMonitor, Armoury Crate, Ryzen Master e similares disputam o mesmo
  acesso de baixo nível. Detectar e avisar o usuário em vez de mostrar zeros.

---

## 9. Pendências antes de fechar o app

1. Validar o acesso ao driver após reiniciar o PC.
2. Identificar o que são as seis temperaturas sem nome da placa-mãe, ou deixar o usuário rotular.
3. Confirmar a legibilidade do painel com transparência ligada sobre fundos claros.
4. Definir como o app se comporta quando outro monitor já segurou o acesso aos sensores.
