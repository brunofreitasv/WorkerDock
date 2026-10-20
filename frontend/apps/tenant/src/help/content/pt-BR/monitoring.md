# Monitore tarefas e execuções

## Pré-requisitos

Entre no painel do tenant. A permissão de leitura é suficiente.

## Permissão

Leitura. O monitoramento de tarefas e execuções é somente para consulta.

## Etapas

1. Abra [Monitoramento](#/monitoring) para pesquisar tarefas, filtrar por estado e percorrer os resultados. A tabela mostra o modelo e a versão, o estado ativo ou arquivado, execuções ativas, falhas recentes e o estado do agendamento.
2. Abra uma execução na lista de execuções recentes do [Painel](#/dashboard) para consultar estado, tentativas, códigos de resultado seguros, certeza e **Linha do tempo**. Os eventos mostram quando o estado da execução mudou.
3. Use **Atualizar** quando disponível. As telas consultam dados enquanto estão visíveis e pausam quando a aba fica oculta; o horário **Atualizado** identifica a última atualização. Se a atualização falhar, o aviso informa que os resultados estão desatualizados. Confira o horário antes de agir.
4. Use os códigos de resultado apenas como resumos. O painel não exibe erros brutos, SQL, configuração nem valores secretos.

O aplicativo pode exibir dados mantidos pelo adaptador configurado. Dados em modo de memória não comprovam que o monitoramento com SQL, a persistência, os bloqueios ou uma implantação real funcionam. Sem um banco SQL configurado, o monitoramento que depende do banco pode ficar indisponível, em vez de aparecer vazio. Consulte [Workers e auditoria](#/help:workers-audit) para outras telas operacionais de consulta.

## Resultado

Você consulta o estado atual da tarefa, as tentativas registradas e a linha do tempo da execução, sabendo quando os dados estão desatualizados ou indisponíveis.
