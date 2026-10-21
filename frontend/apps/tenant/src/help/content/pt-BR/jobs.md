## Pré-requisitos

Você precisa de acesso de gerenciamento e de pelo menos um modelo instalado selecionável. A página **Tarefas** abre um editor com seis etapas: **Modelo**, **Configuração**, **Agendamento**, **Política**, **Revisão** e **Ativação**.

## Função

Criar, editar, ativar, desativar, duplicar, arquivar e interromper tarefas exige acesso de gerenciamento. Usuários com acesso de leitura podem consultar informações de tarefas e execuções quando disponíveis no tenant.

## Etapas

1. Abra [Tarefas](#/jobs), informe um nome e escolha um modelo em **Modelo**.
2. Em **Configuração**, preencha os campos gerados pelo modelo instalado e escolha **Validar configuração**. Corrija os problemas apresentados antes de continuar.
3. Em **Agendamento**, deixe **Usar um agendamento** desativado para uma tarefa manual ou configure um agendamento se necessário.
4. Em **Política**, escolha as opções de tentativas, tempo limite, sobreposição e trabalho perdido. Essas opções controlam a execução; elas não validam o sistema de destino.
5. Revise a tarefa e escolha **Salvar rascunho desativado**. O rascunho salvo permanece desativado e não inicia o trabalho.
6. Revise a configuração salva e escolha **Ativar tarefa** na etapa final quando estiver pronta. Use **Executar agora** para solicitar uma execução manual.
7. Nos detalhes da tarefa, use **Pausar agendamento** ou **Retomar agendamento**, **Desativar tarefa**, **Duplicar** ou **Arquivar** conforme necessário. Uma cópia duplicada fica desativada e precisa ser revisada. Use **Interromper execução** para solicitar a interrupção de uma execução em andamento.

## Exemplo prático

Crie uma tarefa HTTP manual chamada **Verificação diária**, escolha **Requisição HTTP**, informe um endpoint HTTPS permitido e valide a configuração. Deixe **Usar um agendamento** desativado, revise **Política**, salve o rascunho desativado e ative-o depois de verificar o destino e as referências de segredo.

## Verificação

Confirme nos detalhes que o status aparece como ativado somente após a ativação. Use **Executar agora**, anote o ID da execução e consulte o status e a linha do tempo de eventos até chegar a um estado final.

## Recuperação

Mantenha o rascunho desativado enquanto corrige erros de validação. Se a revisão estiver desatualizada, carregue a revisão atual e confira as alterações mantidas antes de salvar. Se a resposta de uma solicitação de execução for incerta, repita com a mesma chave; consulte o histórico antes de criar outra execução com uma nova chave.

## Guias relacionados

Consulte [modelos instalados](#/help:templates), [entradas de tarefas](#/help:job-inputs), [execuções manuais](#/help:manual-runs), [agendamentos](#/help:schedules) e [políticas de execução](#/help:execution-policies).

## Resultado

A tarefa fica salva com o modelo, a configuração, o agendamento e a política escolhidos. A ativação ocorre separadamente depois de salvar o rascunho; o trabalho aceito ou em andamento mantém seu próprio ciclo de vida quando uma tarefa é desativada ou arquivada. Consulte [agendamentos](#/help:schedules), [tarefas do SQL Server](#/help:sql-jobs) e [tarefas HTTP](#/help:http-jobs) para detalhes de configuração.
