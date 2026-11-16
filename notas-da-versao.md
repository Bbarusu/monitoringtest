## Novidades

- A Kisland fechada ocupa só o espaço que aparece: dá para clicar no que está logo abaixo dela, como a barra de endereços do navegador.

## Detalhes

- Kisland: a janela tinha 28 px de margem embaixo e 16 px dos lados para caber a sombra da Kisland aberta. Na fechada, os pixels quase invisíveis da sombra seguravam o clique até uns 30 px abaixo da pílula. Agora, fechada, ela usa margem de 10 px e uma sombra menor (`SombraCompacta`, desfoque 8, deslocamento 2); aberta, volta a margem e a sombra de antes (`AplicarSombra`, chamado em `MostrarEstado` e `AplicarPreta`). A margem de cima não muda, então a pílula não pula.
