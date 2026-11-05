## Novidades

- A tela "O que mudou" das atualizações ficou mais curta: mostra só as novidades que fazem diferença para você.
- A Kisland aberta não é mais cortada embaixo quando tem muita coisa ligada: o que não cabe na tela passa a rolar.

## Detalhes

- O manifesto de atualização (`atualizacao.json`) leva só a seção "Novidades" das notas; a seção "Detalhes" fica apenas na página da release no GitHub.
- O app também corta a seção "Detalhes" e os títulos ao montar a lista, para o caso de um manifesto antigo ou escrito à mão.
- Kisland aberta dentro de uma rolagem (`RolagemExpandida`) com teto na altura útil do monitor, recalculado em `Posicionar`. A barra ocupa a margem direita, sem mudar a largura. A roda do mouse rola a Kisland aberta quando há o que rolar, menos em cima do bloco de volume e do mixer, onde continua mudando o volume.
