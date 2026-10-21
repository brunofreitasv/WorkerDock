# Políticas de execução

## Pré-requisitos

É necessário acesso de gerenciamento e um rascunho de tarefa. As políticas são revisadas durante a criação e a ativação.

## Permissão

Usuários com acesso de leitura podem consultar este guia e o histórico de execuções. Gerentes definem e alteram políticas.

## Etapas

1. Abra [Tarefas](#/jobs), edite um rascunho e acesse **Política**.
2. Defina o tempo limite da tarefa. **Sem limite de tempo** remove o prazo externo da tarefa; os handlers SQL ou HTTP ainda podem ter seu próprio limite.
3. Defina o máximo de tentativas, os atrasos inicial e máximo, a variação aleatória e se resultados incertos podem ser repetidos. Novas tentativas podem repetir efeitos externos; use idempotência no destino quando disponível.
4. Escolha a sobreposição: **Enfileirar**, **Ignorar** ou **Permitir**. Defina o limite da fila para trabalho enfileirado.
5. Para tarefas agendadas, escolha como tratar trabalho perdido: **Ignorar**, **Agrupar** ou **Recuperar**. No editor atual do tenant, **Recuperar** aceita no máximo três ocorrências dentro da janela de recuperação de um dia; o editor oferece a seleção do modo, sem controles para alterar os limites de quantidade ou idade.
6. Revise a política, salve o rascunho desativado e ative a tarefa. Altere a política por meio de uma revisão da tarefa revisada.

## Exemplo prático

Para um relatório que não deve rodar simultaneamente, escolha **Enfileirar**, use um limite de fila pequeno e configure novas tentativas limitadas com variação. Para uma agenda em que só importa o relatório perdido mais recente, escolha **Agrupar** e confira as execuções recuperadas no histórico.

## Verificação

Confirme a política salva na revisão da tarefa e consulte o histórico de tentativas após uma execução. A política controla o tratamento dos resultados pelo executor; não comprova que o destino deduplica efeitos ou consegue desfazê-los.

## Recuperação

Se houver acúmulo, confira o histórico, o limite da fila, a opção de sobreposição, o tempo limite e a política de trabalho perdido. Pause ou desative novas execuções durante o diagnóstico. Interromper uma execução solicita cancelamento e pode não reverter um efeito já aceito pelo destino externo.

## Resultado

A política define novas tentativas limitadas, prazos, sobreposição e recuperação de agendamentos. Execuções aceitas mantêm seu ciclo de vida após alterações no agendamento; a pausa não reproduz o intervalo pausado.

## Guias relacionados

Consulte [agendamentos](#/help:schedules), [execuções manuais](#/help:manual-runs), [criação de tarefas](#/help:jobs) e [monitoramento](#/help:monitoring).
