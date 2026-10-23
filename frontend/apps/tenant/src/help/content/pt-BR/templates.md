# Explore modelos instalados

## Pré-requisitos

Entre no painel do tenant. Acesso de Leitura permite explorar; criar uma tarefa exige a permissão Gerenciar.

## Permissão

Leitura para consultar descrições e versões dos modelos. Gerenciar para selecionar um modelo durante a criação de uma tarefa.

## Etapas

1. Abra [Modelos](#/templates) para consultar o catálogo de modelos instalados. O seletor de modelos do assistente de tarefas mostra o nome de exibição e a versão de cada modelo.
2. Os modelos integrados SQL Server e HTTP oferecem a versão **1.1.0**. Escolha um deles ao criar uma tarefa.
3. Novas instalações incluem os modelos integrados SQL Server e HTTP na versão **1.1.0**. Os pacotes de modelos são instalados pela implantação do aplicativo; a interface do tenant não cria nem instala pacotes.
4. Novos modelos exigem uma alteração de desenvolvimento e implantação. Autores podem seguir `docs/how-to/add-custom-template.md` no repositório de origem.

## Resultado

Você identifica os modelos integrados disponíveis, ambos na versão 1.1.0, e entende que modelos personalizados podem usar outras versões.

## Exemplo prático

Para uma tarefa SQL agendada, selecione **SQL Server** e informe um perfil aprovado, o catálogo, o comando e os parâmetros tipados. Selecionar o modelo apenas inicia a configuração; não conecta ao SQL Server.

## Verificação

Confirme o nome e a versão do modelo na revisão ou nos detalhes da tarefa. Depois da ativação, consulte o status e o histórico de execuções separadamente para confirmar o comportamento em execução.

## Recuperação

Se o modelo não aparecer, peça a um gerente para confirmar se ele está instalado para o tenant. Se um campo não estiver disponível, solicite ao autor do modelo uma atualização de esquema e implantação. Usuários do tenant não podem adicionar modelos pelo catálogo.

## Guias relacionados

Consulte [criação de tarefas](#/help:jobs), [tarefas do SQL Server](#/help:sql-jobs) e [tarefas HTTP](#/help:http-jobs).

Para desenvolver modelos, consulte `docs/how-to/add-custom-template.md` no repositório de origem.
