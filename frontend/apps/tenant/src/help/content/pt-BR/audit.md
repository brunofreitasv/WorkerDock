# Consulte o histórico de auditoria

## O que você vai fazer

Pesquisar os resumos de auditoria do tenant para relacionar ações registradas a um ator e horário.

## Antes de começar

O acesso de Leitura é suficiente. Auditoria é uma tela somente para consulta. A tabela contém campos de resumo; detalhes de payloads e valores secretos não são exibidos.

## Etapas

1. Abra [Auditoria](#/audit). Use **Buscar auditoria** para filtrar os resumos disponíveis. Use **Próxima** e **Anterior** para percorrer as páginas.
2. Consulte **Horário UTC**, **Ação**, **Ator**, **Modo** e **Correlação**. Use o identificador de correlação para relacionar registros da mesma operação e UTC ao comparar horários com outros sistemas.
3. Confira **Atualizado** e qualquer aviso após atualizar. Trate a última resposta bem-sucedida como desatualizada se a tela informar que a atualização falhou.
4. Para mudanças de estado e tentativas de uma execução, abra o guia de [eventos de execução](#/help:execution-events).

## Exemplo

Uma linha sintética pode ser `2026-06-15 14:05 UTC · JobEnabled · user-demo · Session · corr-demo-17`. Ela identifica um resumo de ação e uma chave de correlação; não inclui o payload completo nem comprova que uma execução posterior teve sucesso.

## Verifique o resultado

Você consegue encontrar resumos de ações correspondentes, horários UTC, atores, modos de autenticação e identificadores de correlação. Os resultados são limitados e paginados; registros mais antigos podem não estar disponíveis conforme a retenção configurada no tenant.

## Se não funcionar

Se os dados de auditoria estiverem indisponíveis, atualize e confira **Atualizado**. Uma fonte indisponível não significa um histórico de auditoria vazio. Registros em modo de memória não comprovam persistência de auditoria com SQL. Se faltarem eventos antigos, peça a um administrador que verifique a retenção e a configuração da fonte.

## Guias relacionados

- [Consulte a saúde dos workers](#/help:workers)
- [Consulte eventos de execução](#/help:execution-events)
- [Gerencie retenção e arquivos](#/help:retention)
