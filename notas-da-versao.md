## Novidades

- Kdrop: o QR Code passa a levar ao IP certo em PCs com mais de um endereço na rede, e a página volta a abrir no celular.

## Detalhes

- Kdrop, PC com mais de um IP na mesma placa (um principal por DHCP e outros fixos, de outra faixa): ia para o QR o primeiro da lista, que podia ser o secundário (192.168.0.118 em vez de 192.168.1.118), fora do alcance do celular. Agora cada IP ganha nota, e o da mesma faixa do gateway (pela máscara) vence, seguido do que veio por DHCP.
