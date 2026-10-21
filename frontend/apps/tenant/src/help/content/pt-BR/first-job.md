# Crie e execute sua primeira tarefa

## O que você vai fazer

Usar um modelo instalado para salvar um rascunho, validá-lo, ativar a tarefa e solicitar uma execução manual.

## Antes de começar

Você precisa de acesso de Gerenciar e de um modelo selecionável neste tenant. Escolha uma configuração de exemplo inofensiva e confirme se qualquer destino externo está autorizado antes de ativar o trabalho.

## Etapas

1. Abra [Tarefas](#/jobs). O assistente começa em **Modelo**. Informe um nome reconhecível, como `demo-guia`, em **Nome da tarefa** e escolha um modelo instalado.
2. Avance para **Configuração** e preencha os campos obrigatórios. Use **Validar configuração**; corrija todos os problemas antes de continuar. Uma configuração válida não comprova que uma consulta SQL ou solicitação HTTP externa terá sucesso.
3. Em **Agendamento**, deixe o agendamento desativado para um exemplo manual único. Revise as opções de **Política** e avance para **Revisão**.
4. Confira o resumo, confirme **Revisei esta versão atual** e escolha **Salvar rascunho desativado**. Avance para **Ativação** e escolha **Ativar tarefa** somente depois de conferir a configuração revisada.
5. Escolha **Executar agora**, confirme a janela e anote o identificador de execução aceito. Use o [Painel](#/dashboard) para localizar a execução.

## Exemplo

Crie `demo-guia` usando um modelo de exemplo selecionável e uma entrada sintética, como `região = demo`. Salve e valide o rascunho antes de ativá-lo. Execute-o uma vez somente se o modelo escolhido não tiver efeitos externos indesejados.

## Verifique o resultado

O assistente informa que o rascunho foi salvo, o estado da tarefa passa a **Ativa** e **Executar agora** retorna um identificador de execução aceito. Abra a execução pelo painel e confira o estado registrado e os [eventos](#/help:execution-events); a aceitação não comprova que o efeito externo foi concluído.

## Se não funcionar

Se a validação apontar problemas, corrija os campos indicados e valide novamente. Se o salvamento informar que a revisão está desatualizada, carregue a revisão atual e confira-a antes de salvar. Uma projeção pendente pode atrasar a exibição em Monitoramento. Se não houver modelo selecionável ou um controle estiver indisponível, peça a um administrador que confira os modelos e sua função de Gerenciar.

## Guias relacionados

- [Explore os modelos instalados](#/help:templates)
- [Consulte eventos de execução](#/help:execution-events)
- [Monitore tarefas e execuções](#/help:monitoring)
