## Novidades

- Kdrop: o QR Code passa a levar ao IP certo em PCs com mais de um endereço na rede, e a página volta a abrir no celular.
- Fotos e vídeos na bandeja da Kisland aparecem com a miniatura, em vez do ícone do Windows.

## Detalhes

- Kdrop, PC com mais de um IP na mesma placa (um principal por DHCP e outros fixos, de outra faixa): ia para o QR o primeiro da lista, que podia ser o secundário (192.168.0.118 em vez de 192.168.1.118), fora do alcance do celular. Agora cada IP ganha nota, e o da mesma faixa do gateway (pela máscara) vence, seguido do que veio por DHCP.
- Bandeja: foto ou vídeo (JPG, PNG, GIF, BMP, WebP, HEIC, TIFF, AVIF, MP4, MOV, MKV e outros) mostra a miniatura do Windows (`IconeDoWindows.Miniatura`, `IShellItemImageFactory` com `SIIGBF_THUMBNAILONLY`), feita fora da tela e guardada por caminho e data do arquivo. Sem miniatura do Windows (HEIC sem a extensão da loja, por exemplo), tenta ler a imagem pelo WPF; sem isso, fica o ícone. Bitmap sem nenhum alfa passa a ser lido como opaco (miniatura de foto vinha com o alfa zerado).
