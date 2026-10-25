# KInfo — código completo

Painel de monitoramento de hardware que vive na bandeja do Windows. Abre no
canto inferior direito, ancorado ao ícone da barra de tarefas, e mostra
temperatura, carga, memória, disco e consumo de energia — em cinco abas, com
telas auxiliares para calibração de ventoinhas, processos por uso de GPU,
registro de consumo e teste de estresse.

Este arquivo é a junção de todo o código-fonte do projeto, gerado
automaticamente a cada publicação do instalador. Serve para leitura e revisão:
o repositório continua sendo a fonte.

## A linguagem, e por que ela

**C# sobre .NET 8, interface em WPF.** Windows apenas, de propósito.

A escolha foi determinada por uma restrição concreta: ler temperatura de CPU,
tensões e rotação de ventoinhas exige falar com um driver em modo kernel, e a
única biblioteca madura e aberta que faz isso é a **LibreHardwareMonitorLib**,
escrita em .NET. Qualquer outra linguagem significaria reimplementar o acesso
ao chip da placa-mãe do zero — meses de trabalho e risco de travar a máquina —
ou montar uma ponte até ela, carregando a complexidade das duas coisas.

O resto do app reforça a escolha. Ele conversa o tempo todo com a API do
Windows: contadores de desempenho para uso de GPU por processo, tabelas de
handle do kernel para descobrir quais arquivos um processo mantém abertos,
SMART dos discos, agendador de tarefas, registro, DWM. Em C# isso é P/Invoke,
que é parte da linguagem e não um apêndice.

A interface pediu WPF em vez de WinForms por três coisas que o painel precisa:
janela sem moldura com cantos arredondados, transparência real ajustável pelo
usuário, e consciência de DPI por monitor — o painel é arrastável entre telas
de densidades diferentes. WinForms entrega mal as três.

Um framework web embarcado, tipo Electron, resolveria o visual com menos
esforço, e foi descartado pelo motivo oposto ao da maioria dos projetos: o
objetivo declarado deste app é **não consumir recursos**. Ele roda em 0,05% a
0,07% de CPU e cerca de 88 MB de memória, medidos no hardware de referência. Um
runtime de navegador começa em várias vezes isso, e um monitor de recursos que
pesa mais que o problema que mede não tem razão de existir.

O custo da escolha é ser preso ao Windows. Aqui isso não é limitação, é o
escopo: o app existe para ler sensores de um PC com Windows.

## Convenções

Identificadores e comentários em **português**, sem acento nos nomes de tipos e
membros. Comentários explicam o *porquê* de uma decisão, nunca o que a linha
faz — se um comentário descreve o código, ele foi apagado. Sem MVVM e sem
binding: as telas são atualizadas em código a cada leitura, o que mantém o
caminho entre o sensor e o pixel curto e visível.
