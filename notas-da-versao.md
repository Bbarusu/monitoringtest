## Novidades

- A tela "O que mudou" das atualizações ficou mais curta: mostra só as novidades que fazem diferença para você.
- A Kisland aberta não é mais cortada embaixo quando tem muita coisa ligada: o que não cabe na tela passa a rolar.
- Kdrop: arquivos grandes do celular chegam bem mais rápido, e a Kisland não vai mais para trás das janelas durante um envio.

## Detalhes

- O manifesto de atualização (`atualizacao.json`) leva só a seção "Novidades" das notas; a seção "Detalhes" fica apenas na página da release no GitHub.
- O app também corta a seção "Detalhes" e os títulos ao montar a lista, para o caso de um manifesto antigo ou escrito à mão.
- Kisland aberta dentro de uma rolagem (`RolagemExpandida`) com teto na altura útil do monitor, recalculado em `Posicionar`. A barra ocupa a margem direita, sem mudar a largura. A roda do mouse rola a Kisland aberta quando há o que rolar, menos em cima do bloco de volume e do mixer, onde continua mudando o volume.
- Kdrop: página de diagnóstico em `http://<PC>:48620/teste` (recurso `kdrop-teste.html`), com cinco jeitos de escolher fotos, sem enviar nada. Serve para descobrir se a galeria do iPhone (iOS 26) trava no ✓ por causa do HTTP do Kdrop ou da própria página: os mesmos cinco jeitos funcionaram numa página com HTTPS.
- Kdrop: chaves de diagnóstico na página do celular (`?diag=sem-consulta`, `sem-foco`, `campo`, combináveis com vírgula) para achar o que trava a galeria do iPhone. A `/teste`, servida igual pelo HTTP do Kdrop, funcionou nos cinco jeitos, então o HTTP está descartado.
- Kdrop: o arquivo recebido fica aberto de uma parte para a outra (`ArquivoChegando.Fluxo`), em vez de abrir e fechar a cada 8 MB. Cada fechamento fazia o antivírus reler o arquivo inteiro, e o custo crescia a cada parte. Buffer de leitura de 256 KB e de escrita de 1 MB. O log registra parte lenta (mais de 5 s) e a velocidade média de cada arquivo recebido.
- Kisland: a cada 2 s confere se ainda é "sempre por cima" (WS_EX_TOPMOST) e devolve se tiver perdido, registrando a primeira vez no log. Durante envios pelo Kdrop ela foi vista indo para trás das janelas.
