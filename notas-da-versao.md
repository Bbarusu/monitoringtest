## Novidades

- A capa do que está tocando na Kisland troca junto com o áudio, mesmo quando o título é igual (como áudios de WhatsApp e Telegram).

## Detalhes

- Mídia: a capa era reaproveitada sempre que app, título e artista eram os mesmos da leitura anterior. Áudios de mensagem (Telegram e WhatsApp no navegador) chegam todos como "Mensagem de Voz" e, muitas vezes, com o mesmo artista, então a foto do primeiro ficava presa. Agora cada aviso de troca de conteúdo do Windows (`MediaPropertiesChanged`) obriga a ler a capa de novo, e uma segunda leitura 1,5 s depois pega a imagem que o navegador manda atrasada. Avisos só de posição e de play/pausa continuam reaproveitando a capa.
