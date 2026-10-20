# Consulte workers e eventos de auditoria

## Pré-requisitos

Entre no painel do tenant. A permissão de leitura é suficiente.

## Permissão

Leitura. Essas telas operacionais não alteram a configuração dos workers nem os registros de auditoria.

## Etapas

1. Abra [Workers](#/workers) para consultar o estado, o ID de instalação, o limite atual e o teto de capacidade, o último heartbeat e as capacidades anunciadas por cada worker. O horário do heartbeat ajuda a avaliar se o worker está se comunicando recentemente.
2. Abra [Auditoria](#/audit) para pesquisar e percorrer os eventos registrados. Consulte o horário UTC, a ação, o ator, o modo de autenticação e o ID de correlação para relacionar um evento a uma operação.
3. Use **Atualizar** e o horário **Atualizado** para avaliar a atualidade dos dados. Um aviso pode informar que os dados exibidos estão desatualizados; dados indisponíveis não equivalem a nenhum resultado.
4. Os resumos de auditoria omitem detalhes e valores secretos. Use o ID de correlação ao rastrear atividades relacionadas.

Exemplos em modo de memória podem exibir registros mantidos pelo adaptador, mas não verificam a persistência de auditoria em SQL nem a coordenação real dos workers. Para consultar tentativas e a [linha do tempo](#/help:monitoring) de uma execução, abra o guia de monitoramento.

## Resultado

Você consulta heartbeats e capacidade dos workers e pesquisa resumos de auditoria sem alterar o estado do tenant.
