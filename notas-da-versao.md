## Novidades

- Microfone no mixer da Kisland: ao abrir o mixer, ajuste o volume do microfone, desligue ou ligue com um toque e escolha qual microfone usar.

## Detalhes

- Mixer: linha do microfone no topo, só com o mixer aberto. Volume e mudo do microfone padrão do Windows (`VolumeService` com `microfone: true`, que usa o fluxo de captura em vez do de saída), o ícone apaga quando está mudo, e subir o volume tira o mudo. A roda do mouse sobre a linha também ajusta. Com mais de um microfone ligado, aparecem os botões para escolher o padrão (o mesmo `IPolicyConfig` das saídas de som); o desenho dos botões de saída e de entrada passou a ser um só (`ChipDeAparelho`).
- Limpeza: espaços que sobraram onde havia comentários dentro de chamadas (`GetDefaultAudioEndpoint(0 , 1 , …)`).
