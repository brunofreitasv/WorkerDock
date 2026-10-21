# Explore modelos instalados

## Pré-requisitos

Entre no painel do tenant. Acesso de Leitura permite explorar; criar uma tarefa exige a permissão Gerenciar.

## Permissão

Leitura para consultar descrições e versões dos modelos. Gerenciar para selecionar um modelo durante a criação de uma tarefa.

## Etapas

1. Abra [Modelos](#/templates) para consultar o catálogo de modelos instalados. O seletor de modelos do assistente de tarefas mostra o nome de exibição e a versão de cada modelo.
2. Escolha um modelo selecionável ao criar uma tarefa. Uma versão aposentada continua visível para tarefas existentes, mas fica desativada para novas seleções.
3. Os pacotes de modelos são instalados pela implantação do aplicativo. A interface do tenant não cria, envia nem instala pacotes de modelos. Desenvolvedores que adicionam um modelo personalizado devem seguir `docs/how-to/add-custom-template.md` no repositório de código-fonte.

## Resultado

Você identifica o modelo instalado e a versão disponível para uma tarefa e entende por que uma versão aposentada não pode ser selecionada em uma tarefa nova.

## Exemplo prático

Para uma tarefa SQL agendada, selecione **SQL Server** e informe um perfil aprovado, o catálogo, o comando e os parâmetros tipados. Selecionar o modelo apenas inicia a configuração; não conecta ao SQL Server.

## Verificação

Confirme o nome e a versão do modelo na revisão ou nos detalhes da tarefa. Depois da ativação, consulte o status e o histórico de execuções separadamente para confirmar o comportamento em execução.

## Recuperação

Se o modelo não aparecer, peça a um gerente para confirmar se ele está instalado para o tenant. Se um campo não estiver disponível, escolha uma versão compatível ou solicite ao autor do modelo uma atualização de esquema e implantação. Usuários do tenant não podem adicionar modelos pelo catálogo.

## Guias relacionados

Consulte [criação de tarefas](#/help:jobs), [tarefas do SQL Server](#/help:sql-jobs) e [tarefas HTTP](#/help:http-jobs).
