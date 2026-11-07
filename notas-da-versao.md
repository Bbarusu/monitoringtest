## Novidades

- Kdrop: o QR Code passa a levar ao IP certo em PCs com mais de um endereço na rede, e a página volta a abrir no celular.
- Fotos e vídeos na bandeja da Kisland aparecem com a miniatura, em vez do ícone do Windows.
- A Kisland não fica mais escondida atrás de outras janelas depois de um tempo.

## Detalhes

- Kdrop, PC com mais de um IP na mesma placa (um principal por DHCP e outros fixos, de outra faixa): ia para o QR o primeiro da lista, que podia ser o secundário (192.168.0.118 em vez de 192.168.1.118), fora do alcance do celular. Agora cada IP ganha nota, e o da mesma faixa do gateway (pela máscara) vence, seguido do que veio por DHCP.
- Bandeja: foto ou vídeo (JPG, PNG, GIF, BMP, WebP, HEIC, TIFF, AVIF, MP4, MOV, MKV e outros) mostra a miniatura do Windows (`IconeDoWindows.Miniatura`, `IShellItemImageFactory` com `SIIGBF_THUMBNAILONLY`), feita fora da tela e guardada por caminho e data do arquivo. Sem miniatura do Windows (HEIC sem a extensão da loja, por exemplo), tenta ler a imagem pelo WPF; sem isso, fica o ícone. Bitmap sem nenhum alfa passa a ser lido como opaco (miniatura de foto vinha com o alfa zerado).
- Kisland atrás das janelas: o log de quem usa mostrou que o "sempre por cima" nunca se perdia, então outra janela passava à frente dela. A cada 2 s ela olha quem está acima na ordem do Windows e sobre a área dela (`QuemCobre`, por `GW_HWNDPREV`), ignorando janelas encobertas, transparentes ao clique e as do próprio Windows (menus, dicas, barra de tarefas, Iniciar, Alt+Tab). Achando uma, volta para o topo e registra no log o processo, a classe, se é "sempre por cima" e o tamanho, uma vez por janela. Janela do tamanho do monitor inteiro (jogo ou vídeo em tela cheia) só vai para o log, sem briga. Ao reaparecer (PC parado, tela cheia, pedido do Kdrop), ela também se põe no topo na hora.
