# Configure alertas

## Pré-requisitos

É necessária a permissão Gerenciar. Antes de criar um alerta, configure a política de saída do tenant para permitir o host e a porta de destino e verifique se o provedor de segredos pode resolver as referências lógicas usadas nos cabeçalhos do webhook. O destino é validado pela política de saída quando a entrega é executada.

## Permissão

Gerenciar para criar ou alterar destinos webhook e regras, ativar regras ou tentar novamente uma entrega esgotada. Usuários com acesso de Leitura podem apenas consultar incidentes e entregas.

## Etapas

1. Abra [Alertas](#/alerts) e crie um destino webhook. Informe o nome e a URL HTTPS, os nomes opcionais de cabeçalho com **referências lógicas de segredo** e as configurações de tentativas e tempo limite. Não informe valores secretos dos cabeçalhos. Você pode desativar o destino antes de salvar; o destino sozinho não envia notificações.
2. Crie uma regra de alerta e selecione um destino ativo. Escolha uma condição compatível e defina limite, janela de consulta, período de carência e intervalo de lembretes dentro dos limites dos campos. Regras de longa duração e worker indisponível exigem limite igual a um.
3. Salve a regra. O primeiro salvamento a cria **desativada**. Revise a regra salva e depois use **Ativar** como uma ação separada e explícita.
4. Consulte incidentes e tentativas de entrega na tela Alertas. O estado da entrega e os códigos de resultado seguros são somente para consulta. Um gerente pode tentar novamente uma entrega com falha ou esgotada quando o controle estiver disponível; a nova tentativa preserva a identidade da entrega e pode enviar outra requisição HTTP.

A avaliação de alertas, a persistência de incidentes e o processamento das entregas usam SQL do tenant e o worker da fila de saída. O modo de memória ou um salvamento na interface não comprovam avaliação SQL nem entrega de webhook em produção. O adaptador de desenvolvimento do cofre não oferece um serviço de segredos de produção; verifique o provedor confiável configurado antes de depender de entregas com segredos. Webhooks têm entrega pelo menos uma vez, portanto o destinatário deve deduplicar pelo ID da entrega. Consulte a [política de saída](#/settings) para controles de destino.

## Resultado

Uma regra ativa pode abrir incidentes e enfileirar entregas webhook pelo serviço configurado com SQL. Usuários de Leitura consultam o estado dos incidentes e entregas sem visualizar ou alterar credenciais do destino.
