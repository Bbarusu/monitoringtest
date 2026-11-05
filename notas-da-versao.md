## Novidades

- A tela "O que mudou" das atualizações ficou mais curta: mostra só as novidades que fazem diferença para você.
- A Kisland aberta não é mais cortada embaixo quando tem muita coisa ligada: o que não cabe na tela passa a rolar.

## Detalhes

- O manifesto de atualização (`atualizacao.json`) leva só a seção "Novidades" das notas; a seção "Detalhes" fica apenas na página da release no GitHub.
- O app também corta a seção "Detalhes" e os títulos ao montar a lista, para o caso de um manifesto antigo ou escrito à mão.
- Kisland aberta dentro de uma rolagem (`RolagemExpandida`) com teto na altura útil do monitor, recalculado em `Posicionar`. A barra ocupa a margem direita, sem mudar a largura. A roda do mouse rola a Kisland aberta quando há o que rolar, menos em cima do bloco de volume e do mixer, onde continua mudando o volume.
- Kdrop: página de diagnóstico em `http://<PC>:48620/teste` (recurso `kdrop-teste.html`), com cinco jeitos de escolher fotos, sem enviar nada. Serve para descobrir se a galeria do iPhone (iOS 26) trava no ✓ por causa do HTTP do Kdrop ou da própria página: os mesmos cinco jeitos funcionaram numa página com HTTPS.
