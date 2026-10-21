# Consulte a saúde dos workers

## O que você vai fazer

Verificar se os workers estão enviando atualizações recentes e entender a capacidade e as competências exibidas.

## Antes de começar

O acesso de Leitura é suficiente. O estado dos workers é uma tela operacional somente para consulta; ela não permite alterar configuração ou capacidade.

## Etapas

1. Abra [Workers](#/workers). Consulte **Estado**, **ID**, **Capacidade**, **Heartbeat** e **Capacidades** de cada worker listado.
2. Compare os horários de **Heartbeat** com o horário atual. Analise `limite ativo / teto de capacidade` junto com o estado; uma capacidade anunciada descreve o que o worker pode aceitar, não se uma tarefa foi concluída.
3. Confira **Atualizado** e qualquer aviso após uma falha de atualização. Um resultado desatualizado é a última resposta bem-sucedida, não um estado atual.
4. Abra [Auditoria](#/audit) para pesquisar eventos operacionais registrados ou consulte os [eventos de execução](#/help:execution-events) de uma execução específica.

## Exemplo

Uma linha sintética pode mostrar `Ready · worker-demo · 2/4 · heartbeat 08:55 UTC · sql,http`. Se a página foi atualizada às 09:00 UTC, o heartbeat tem cinco minutos; siga o limite operacional do seu tenant, pois este exemplo não define um limite universal.

## Verifique o resultado

Você consegue identificar o estado e o heartbeat informados pela última resposta e saber se os valores podem estar desatualizados ou indisponíveis. Nenhum worker pode significar que nenhum registro foi retornado; isso não diagnostica o motivo por si só.

## Se não funcionar

Se a tela informar que os dados estão indisponíveis, atualize e confira **Atualizado**. Uma indisponibilidade persistente pode indicar que a fonte de monitoramento ou a projeção de workers não está configurada. Uma resposta em modo de memória não comprova coordenação real dos workers nem persistência com SQL. Peça a um administrador que verifique a saúde do serviço e a configuração da implantação.

## Guias relacionados

- [Entenda o painel](#/help:dashboard)
- [Monitore tarefas e execuções](#/help:monitoring)
- [Consulte o histórico de auditoria](#/help:audit)
