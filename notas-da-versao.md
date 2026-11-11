## Novidades

- A capa do que está tocando na Kisland troca junto com o áudio, mesmo quando o título é igual (como áudios de WhatsApp e Telegram).
- Quando sai uma versão nova, o KInfo avisa assim que é aberto: o painel abre com as novidades e chega uma notificação do Windows.

## Detalhes

- Mídia: a capa era reaproveitada sempre que app, título e artista eram os mesmos da leitura anterior. Áudios de mensagem (Telegram e WhatsApp no navegador) chegam todos como "Mensagem de Voz" e, muitas vezes, com o mesmo artista, então a foto do primeiro ficava presa. Agora cada aviso de troca de conteúdo do Windows (`MediaPropertiesChanged`) obriga a ler a capa de novo, e uma segunda leitura 1,5 s depois pega a imagem que o navegador manda atrasada. Avisos só de posição e de play/pausa continuam reaproveitando a capa.
- Atualizações: a procura deixou de ser no máximo uma vez por dia. Agora acontece a cada início do app (com até 3 novas tentativas, de minuto em minuto, se a internet ainda não estiver pronta, como logo após ligar o PC) e de 6 em 6 horas com o app aberto. Achando versão nova ao iniciar, o painel abre com o cartão da atualização; em qualquer caso, uma notificação do Windows (balão da bandeja) avisa uma vez por versão, e tocar nela abre o painel. A opção "Procurar atualizações" da Configuração continua desligando tudo isso.
